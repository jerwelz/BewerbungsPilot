# Security: accounts, data isolation and migration (Milestone 9)

BewerbungsPilot stores private data: applications, notes, PDFs, interviews and workplace positions.
Since M9 every user needs an account, and every user sees only their own data. This document
describes how that is enforced, what the guarantees and limits are, and how to migrate an
existing (pre-M9) database safely.

## 1. Accounts and sessions

| Topic | Implementation |
|---|---|
| Accounts | ASP.NET Core Identity (`IdentityUser`), tables `AspNet*` in the same SQLite database. E-mail address = user name, unique. |
| Passwords | Identity password hasher (PBKDF2-HMAC-SHA512, salted). At least 12 characters, no composition rules. |
| Lockout | 5 failed logins → account locked for 15 minutes. Unknown e-mail and wrong password give the same answer. |
| Rate limiting | Login, registration, password change: 10 requests per minute per IP address (`Auth:RequestsPerMinute`) → 429. |
| Login cookie | HttpOnly, `SameSite=Strict`; outside Development `Secure` and name `__Host-BewerbungsPilot`. 8 h sliding expiration (`Auth:SessionLifetime`); "Angemeldet bleiben" = fixed 14 days. |
| Logout | Ends **all** sessions of the account (new security stamp), so a copied cookie is worthless too. |
| Password change | Requires the current password; ends all *other* sessions immediately. |
| Registration | Only when `Auth:AllowRegistration` is `true` (Development: on; default/Production: off). |
| Password reset | No e-mail in M9. Operator command `reset-password` (also lifts a lockout). |
| E-mail verification | Not needed while registration is operator-controlled and no e-mail reset exists. **Required** before adding e-mail reset or open registration. |
| CSRF | Every POST/PUT/PATCH/DELETE (also login and registration) needs the antiforgery token in the `X-CSRF-TOKEN` header (`ApiProtectionFilter`). Token from `GET /api/auth/antiforgery`, bound to the user. `SameSite=Strict` is a second layer. |
| Keys | ASP.NET Core Data Protection keys in `storage/keys` (Windows: DPAPI-encrypted). Losing them only logs everybody out. |

All endpoints require a login (`FallbackPolicy`), except `GET /api/auth/me` (answers 401),
`GET /api/auth/antiforgery`, `POST /api/auth/register` and `POST /api/auth/login`.
The static HTML/JS files are public: they contain no data.

## 2. Data isolation

Ownership is stored only on `JobApplication.OwnerId`. History entries, documents and interviews
belong to the owner of their application (required foreign key, cascade delete). Deadlines and
confirmed map positions are columns of the application.

Two layers in `Data/AppDbContext.cs`:

1. **Global query filters** on all four entity types: only rows whose application belongs to the
   current user. No user (anonymous request, command line) → no rows ("fail closed").
2. **Ownership guard in `SaveChanges`**: before anything is written, every added, modified or
   deleted application, history entry, document and interview is checked **against the database**
   (through the filtered sets, not the possibly forged values in the change tracker):
   - a new application gets the current user as owner; a different owner is rejected,
   - the owner of an application can never change,
   - a child row may only be added to an application of the current user,
   - a modified or deleted child row must be stored under an application of the current user,
   - a child row can never be moved to another application.

   A violation throws `OwnershipViolationException` before anything is written; the API answers
   **404**, exactly like for an id that does not exist (no id enumeration).

### Guarantees and limitations of the query filters

Filters apply to: every LINQ query on the four sets (incl. `Include`, navigation projections,
`Count`/`Any`, aggregates), the database query of `Find`, queries composed on `FromSql`, and the
source query of `ExecuteUpdate/ExecuteDelete`.

They do **not** protect against:

| Limitation | How it is covered |
|---|---|
| Writes (`Add` with any foreign key, `Attach`, `State = Modified`) are not filtered | Ownership guard (above) |
| `IgnoreQueryFilters`, raw SQL (`ExecuteSql*`, ADO.NET) bypass filters | Allowed only in the files listed below; enforced by `SecurityConventionTests` |
| `ExecuteUpdate/ExecuteDelete` bypass the `SaveChanges` guard | Same allow-list |
| `Find` returns an already tracked entity without a query | A context belongs to exactly one request/user (scoped). **No DbContext pooling** (convention test) |
| A filter value captured from a local variable would be frozen for all users | The filter reads the context property `CurrentUserId`; test with several users in one process |
| Files on disk are not protected by filters | Files are only reached through a filtered document row |
| A new entity type needs its own filter | Convention test: every entity with `OwnerId`/`JobApplicationId` has a filter |

Allowed exceptions (everything else fails `SecurityConventionTests`):

| API | Only in | Purpose |
|---|---|---|
| `IgnoreQueryFilters`, `ExecuteUpdate` | `Logic/LegacyDataClaim.cs` | Command-line claim of pre-M9 data |
| `ExecuteDelete` | nowhere | – |
| Raw SQL / `SqliteConnection` | `Data/MigrationSafety.cs` | Read-only checks, `VACUUM INTO` backup |
| `File`/`Directory`/`FileStream` | `Logic/DocumentStorage.cs`, `Data/MigrationSafety.cs`, `Program.cs` | Document files, backups, data directory |

## 3. Endpoint authorization matrix

"Owner" = record loaded through the query filter; a foreign or unclaimed id → 404. CSRF = token required.

| Method | Route | Ownership | CSRF |
|---|---|---|---|
| GET | /api/auth/me | own account (401 if anonymous) | – |
| GET | /api/auth/antiforgery | anonymous | – |
| POST | /api/auth/register · /api/auth/login | anonymous, rate-limited | yes |
| POST | /api/auth/logout · /api/auth/password | logged in | yes |
| GET | /api/applications · /duplicates · /api/dashboard · /api/calendar · /api/calendar/upcoming · /api/timeline · /api/waiting · /api/analysis | only own rows | – |
| POST | /api/map/query | only own rows | yes |
| GET | /api/applications/{id} · /{id}/documents · /api/documents/{id}/preview (+HEAD) · /download | owner | – |
| POST | /api/applications | owner = caller (body cannot set it) | yes |
| PUT/PATCH/DELETE | /api/applications/{id} · /{id}/status · /{id}/map-location | owner + guard | yes |
| POST | /api/applications/{id}/documents · /{id}/interviews | owner of the application + guard | yes |
| PUT/DELETE | /api/interviews/{id} · DELETE /api/documents/{id} | owner + guard | yes |
| POST | /api/geocode | logged in; cache per user; 403 in demo mode | yes |
| POST | /api/demo/start | anonymous, own rate limit (5 per IP and hour); **only mapped in demo mode** (§11) | yes |
| GET | /health (not under /api) | anonymous; answers only `Healthy`/`Unhealthy`, reads no user data | – |
| GET | /willkommen.html and all other static files | anonymous (contain no data; pages load data via the API) | – |

`AuthorizationTests.EveryApiEndpoint_IsInTheAuthorizationMatrix` fails if an endpoint is added
without being added to the tested matrix.

## 4. Documents

- Files live in `storage/documents/` (outside `wwwroot`) under random GUID names; the name comes
  only from the database row and is checked by a strict pattern, so paths cannot be manipulated.
- Upload, list, preview, download and delete all load the document or its application through
  the query filter first. Another user's id → 404, no file is opened, written or deleted.
- Responses: `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`.

## 5. Browser protections

- All `/api` responses and all HTML pages: `Cache-Control: no-store` (also keeps pages out of the
  back/forward cache).
- 401 → start page `willkommen.html` (`?returnUrl=…&expired=1`), which forwards to the login page
  at once when demo mode is off; private content is hidden first. `returnUrl` only accepts own paths
  (no open redirect).
- Content-Security-Policy on every response: scripts and styles only from the app itself (no inline
  scripts; inline `style` attributes allowed), images additionally from `tile.openstreetmap.org`,
  frames only same-origin, no plugins. Plus `Permissions-Policy` (no camera, microphone,
  geolocation, payment, USB) and `Cross-Origin-Opener-Policy: same-origin`.
- Map tiles are requested with `referrerpolicy=strict-origin-when-cross-origin`: OpenStreetMap
  receives the site's origin (required by its tile policy), never the page path.
- "Back" after logout: pages restored from the cache check the session before showing anything.
- Login/logout in another tab → all other tabs reload (`localStorage` signal `bewerbungspilot.session`).
- A stale tab can never save into another account: the antiforgery token is bound to the user,
  and `api.js` reloads instead of writing when the token belongs to a different user.
- The map starting point is stored per user (`bewerbungspilot.origin.<userId>`) and removed on logout;
  the shared pre-M9 key is deleted.

## 6. Operator commands

Run on the computer that holds the data (never reachable over HTTP). Add
`--Storage:DataDirectory <folder>` to work on another data folder, e.g. a copy.

```
dotnet run --project src/BewerbungsPilot -- migrate
dotnet run --project src/BewerbungsPilot -- claim-legacy-data --email you@example.com
dotnet run --project src/BewerbungsPilot -- reset-password --email you@example.com
```

**Startup behaviour:** a *new* database is created automatically. An *existing* database with
pending migrations is **not** migrated at startup – the app refuses to start and asks for `migrate`.

**`migrate`** (`Data/MigrationSafety.cs`), every step must succeed:

1. integrity and foreign-key check, snapshot (row counts + SHA-256 over every value of every table),
   document manifest (every file's SHA-256 against `Documents.Sha256`);
2. consistent backup with `VACUUM INTO` into `storage/backups/<UTC time>-pre-<migration>/`;
3. backup validation: opens read-only, integrity ok, same migration level and identical snapshot;
4. copy of every document file into the backup folder, each copy re-hashed; `manifest.json`;
5. migration;
6. verification: same row counts and identical values in every pre-existing column, no foreign-key
   violations, no application assigned to anybody, every document file unchanged.

If 1–4 fail, the migration is not started and the database is untouched. If 5–6 fail, the
command stops with exit code 2 and prints the exact restore command; nothing is restored
automatically. Missing or changed document files that already existed before are reported as
warnings (they are not caused by the migration).

**`claim-legacy-data`** (`Logic/LegacyDataClaim.cs`): pre-M9 rows have `OwnerId = NULL` and are
invisible to everybody. Registering an account – also the first one – never gives it this data.
The command shows what will be assigned, asks you to type the e-mail address again, assigns
everything in one transaction, verifies the counts (else rollback) and is idempotent.

## 7. Migrating the original data (runbook)

Do this only when you decide to. Until then the original `storage/` stays as it is
(a pre-M9 build keeps working with it; the M9 build refuses to start on it).

1. **Stop** every running BewerbungsPilot instance (also `dotnet run` in other terminals).
2. **Manual backup**: copy the whole folder `src/BewerbungsPilot/storage` (database, `-wal`/`-shm`
   if present, `documents/`) to a folder outside the repository, e.g.
   `%USERPROFILE%\Documents\BewerbungsPilot-Backup-before-M9\`.
3. `dotnet run --project src/BewerbungsPilot -- migrate` – read the report: counts before/after,
   backup folder, document check. Exit code 0 = everything verified.
4. `dotnet run --project src/BewerbungsPilot` (Development: registration is on), open the app,
   register **your** account.
5. Stop the app, then `dotnet run --project src/BewerbungsPilot -- claim-legacy-data --email <your e-mail>`
   and confirm. The report must show the same numbers as step 3.
6. Start the app, log in, check every page and open/download your PDFs.

**Rollback**: stop the app; copy the backup database (step 3's folder or your manual backup)
over `storage/bewerbungspilot.db`, delete `bewerbungspilot.db-wal`/`-shm` if present, and use the
M8 version (`git checkout m8`). Documents are never moved or renamed by M9.

## 8. Before exposing BewerbungsPilot to the internet

The public **demo** configuration for Fly.io (`fly.toml`, `Dockerfile`, docs/deployment.md) follows
this list. Mandatory:

- HTTPS (reverse proxy with TLS or Kestrel certificate) and HSTS (enabled outside Development).
  Behind a proxy that forwards plain HTTP, set `ASPNETCORE_HTTPS_PORT=443` so the HTTPS redirect
  works. `/health` is exempt from the redirect (the platform checks it over the internal network).
- Run with `ASPNETCORE_ENVIRONMENT=Production` (Secure + `__Host-` cookies; plain HTTP logins then
  fail on purpose; errors answer a German ProblemDetails message without internals).
- `AllowedHosts` set to the real host name. A public demo refuses to start with `AllowedHosts=*`
  or with a relative data folder (third demo safeguard, §11).
- Behind a reverse proxy: `ReverseProxy:KnownProxies` set to the proxy address(es) – otherwise
  rate limiting sees only the proxy IP, and HTTPS is not detected. Where the proxy address is not
  known but the app port is reachable **only** through the host's proxy (Fly.io), use
  `ReverseProxy:TrustAllProxies=true` together with `ReverseProxy:ClientIpHeader=Fly-Client-IP`
  (a header the proxy sets to exactly one client address). The app logs a warning at startup.
  Checked on the live instance (2026-10-23): the demo-start limit counted IPv4 and IPv6 of the same
  computer separately, and forged `Fly-Client-IP` / `X-Forwarded-For` headers did not get around it
  (Fly's proxy overwrites them).
- Request bodies are limited to 1 MB (only the PDF upload allows ~12 MB).
- `Auth:AllowRegistration` = `false` except while creating accounts.
- On Linux/macOS the Data Protection keys in `keys/` are **not encrypted** (DPAPI exists only on
  Windows): restrict the folder to the service account, or configure a key-encryption certificate.
- A persistent disk for the data folder (container file systems are often lost on restart/redeploy).
- `storage/` (database, documents, keys, backups) readable/writable only by the service account,
  outside any web root; regular off-machine backups of the database (`VACUUM INTO`), `documents/`
  and `keys/`.
- Logs: keep `System.Net.Http.HttpClient` at `Warning`; never log request bodies (they contain
  passwords, search texts and starting points).

Recommended: rate limiting at the proxy, monitoring of failed logins,
e-mail-based password reset with e-mail verification once an e-mail provider exists.

## 9. Known limitations

- Logout ends the sessions of the account on all devices (consequence of server-side invalidation).
- The lockout reveals that an account exists (only for registered e-mails); registration also
  reports an already used e-mail. Acceptable while registration is operator-controlled.
- No two-factor authentication, no account deletion, no admin interface, no e-mail flows.
- The Nominatim limit (1 request/second) is shared by all users of one server.
- Every response renews the login cookie (security stamp checked on every request).
- Data Protection keys on Windows are bound to the Windows user (DPAPI); restoring `keys/` on
  another machine only logs everybody out.

## 10. Tests

- xUnit (`dotnet test`): ownership and isolation per service, unauthorized child-record
  modifications, convention tests, migration safety on populated databases, claim, HTTP matrix
  (anonymous, foreign ids, aggregates, CSRF, caching headers, endpoint coverage), login/lockout/
  rate limit/password change/session expiration, startup guard.
- Browser (`tests/e2e`, `npm install` then `node run.mjs`): the M2–M8 regression suites (each as
  its own logged-in user) plus `m9.mjs` (registration, login, logout, back navigation, CSRF from
  another site, cross-user isolation on every page, document access, account switching, stale tabs)
  and `m9-expiry.mjs` (real 15-second session expiration), `m10.mjs` (demo mode: start, banner on
  every page, blocked upload, isolation of two visitors), `m11.mjs` (start page, demo start, every
  page and the PDF preview **without CSP violations**, logout, mobile). m2–m9 run with Playwright's
  `bypassCSP`, because `waitForFunction` needs `eval`, which the CSP forbids.
- xUnit `PublicDeploymentTests` (M11): start page and `/health` anonymous, production safeguard,
  HTTPS redirect (not for `/health`), HSTS/CSP/Permissions-Policy, `__Host-` Secure cookies, unknown
  host names refused, registration closed, ProblemDetails without internals, forwarded headers only
  when configured (and per-visitor rate limits behind the proxy), demo start limit, write limit,
  per-account limits, long texts, cleanup service, data and login surviving a restart.
- `tests/container/smoke.mjs` (CI job "container", Linux): the Docker image in Production demo mode
  behind a simulated proxy – no data in the image, not running as root, health, HTTPS redirect,
  demo start, PDF from the volume, registration closed, 1 MB limit, persistence across a restart.
- xUnit `DemoTests`: demo endpoint absent when off, isolation of demo visitors, blocked
  registration/upload/place search, no password login, account limit, expiry cleanup with files,
  both startup safeguards, consistency of the seed data.

## 11. Demo mode

`Demo:Enabled=true` turns the instance into a demonstration with fictional data
(`Logic/DemoAccounts.cs`, `Logic/DemoSeeder.cs`, `Endpoints/DemoEndpoints.cs`).

| Topic | Implementation |
|---|---|
| Entry | Start page `willkommen.html` → `POST /api/demo/start` (anonymous, antiforgery token, at most `Demo:StartsPerHour` = 5 starts per client IP and hour). Not mapped when demo mode is off. |
| Accounts | One new account per visitor, `demo-<random>@demo.invalid`, **without password** – it cannot be used with the login form. Marked by the user claim `bewerbungspilot:demo-created-at` (no schema change). Created together with the claim in one transaction. |
| Isolation | The same query filters and ownership guard as for real accounts; the seed data is written through a context of the demo user. Visitors never see each other's data. |
| Lifetime | Accounts older than `Demo:AccountLifetime` (default 4 h) are deleted – applications, children, document files, user – whenever the next demo starts **and** every `Demo:CleanupInterval` (default 10 min) by `DemoCleanupService`. Only accounts with the demo claim are considered. A deleted account's session ends at its next request (security stamp check). |
| Limits | At most `Demo:MaxAccounts` (default 200) demo accounts → 429. Per account at most 50 applications and 100 interviews (403); at most `Demo:WritesPerMinute` (default 60) changing API requests per visitor and minute (429). Text lengths are validated, request bodies ≤ 1 MB. Worst case per account: about 1.1 million characters of text plus small history rows – a few MB. 200 accounts filled to the limit could approach the 1 GB volume; normal use stays far below. Watch the volume usage and enlarge it if needed (docs/deployment.md). |
| Restrictions | Registration off (overrides `Auth:AllowRegistration`), uploads 403, place search 403 (no requests to Nominatim for anonymous visitors). |
| Safeguards | The app refuses to start in demo mode (1) when the data folder is the default `storage/` folder – checked before anything in it is opened – (2) when the database contains an account without the demo claim, and (3) outside Development, when `AllowedHosts` is missing or `*`, or the data folder is not an absolute path. |
| Logging | "Demo-Konto erstellt", refused starts and the number of deleted accounts – no ids, addresses or IPs. |

Run locally: `dotnet run --project src/BewerbungsPilot --launch-profile demo` (data in `storage-demo/`).
