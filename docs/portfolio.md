# Portfolio notes

Material for presenting BewerbungsPilot on GitHub, in a CV and in interviews. Everything here
describes what is in the repository. Please check and adjust the wording so it matches your own
part in the work.

## Screenshots

The screenshots in `docs/screenshots/` come from the demo mode and show **only fictional data**.
To renew them:

```bash
dotnet run --project src/BewerbungsPilot --launch-profile demo   # terminal 1
cd tests/e2e && node screenshots.mjs                              # terminal 2
```

The script refuses to run if the app is not in demo mode.

| File | Page | Use |
|---|---|---|
| `01-uebersicht.png` | Dashboard | **README header**, GitHub social preview |
| `03-pipeline.png` | Kanban pipeline | shows interaction (drag and drop) |
| `08-detail.png` | Application list + detail panel | shows the core CRUD workflow |
| `05-verlauf.png` | Timeline | shows the history / data model |
| `07-standorte.png` | Map | visually striking; mentions OpenStreetMap |
| `00-anmeldung.png`, `02`, `04`, `06` | Login, list, calendar, analysis | linked from the README |

Social preview (GitHub → Settings → General → Social preview): crop `01-uebersicht.png` to 1280×640.

## GitHub repository

**Description** (≤ 350 characters):

> Bewerbungsmanager als Webanwendung: Bewerbungen, Kanban-Pipeline, Dokumente, Termine, Verlauf und Karte. ASP.NET Core 10, EF Core, SQLite, Vanilla JS. Benutzerkonten mit Datenisolation, Demo-Modus mit fiktiven Daten, xUnit- und Playwright-Tests.

**Topics:** `aspnet-core` `csharp` `dotnet` `entity-framework-core` `sqlite` `minimal-api`
`aspnet-core-identity` `javascript` `xunit` `playwright` `kanban` `job-application-tracker`
`portfolio-project`

## CV entry (German)

> **BewerbungsPilot – Webanwendung zur Bewerbungsverwaltung** (Portfolio-Projekt, 2026)
> Webanwendung mit ASP.NET Core 10 (Minimal APIs), Entity Framework Core und SQLite; Frontend in
> HTML/CSS/JavaScript ohne Framework. Funktionen: Bewerbungsverwaltung, Kanban-Pipeline,
> PDF-Dokumente, Termine, Statusverlauf, Kartenansicht. Benutzerkonten mit ASP.NET Core Identity
> und Datenisolation über EF-Core-Abfragefilter; 286 xUnit-Tests, 11 Browser-Testsuiten (Playwright) und ein Container-Smoke-Test.
> Entwickelt mit Unterstützung eines KI-Assistenten (Claude Code).
> Demo: https://bewerbungspilot.fly.dev · GitHub: https://github.com/jerwelz/BewerbungsPilot

## Interview talking points

Questions you can expect, and where the answer is in the code:

1. **"What does the application do, and why did you build it?"** It is a tracker for your own
   applications, built for the actual situation of applying for an apprenticeship. It was planned in
   milestones, each with acceptance criteria (Git tags `m1`–`m10`).
2. **"How is the code structured?"** Three layers: `Endpoints/` (HTTP), `Logic/` (rules), `Data/`
   (EF Core). Example path: drag a card in the pipeline → `PATCH /api/applications/{id}/status`
   → `ApplicationService.ChangeStatusAsync` → status and history entry in **one** `SaveChanges`.
3. **"How do you make sure users only see their own data?"** `AppDbContext`: global query filters
   on `OwnerId`, plus an ownership check in `SaveChanges` for writes. The filter reads a property of
   the context instance. A captured variable would be frozen for all users. Another user's id
   gives 404, not 403, so ids cannot be enumerated.
4. **"Why SQLite?"** One file, no server, simple backups; enough for one person. Limits: one
   instance, few parallel writers. Switching to PostgreSQL would mainly mean changing the EF Core
   provider and the migrations.
5. **"How did you protect the existing data during a schema migration?"** The `migrate` command
   (`MigrationSafety.cs`) makes a backup with `VACUUM INTO`, validates it, migrates, and then compares
   row counts and SHA-256 snapshots.
6. **"How did you test?"**
   - Services against a real in-memory SQLite database (not the EF in-memory provider).
   - HTTP tests with `WebApplicationFactory`.
   - An authorization matrix that fails when a new endpoint has no test.
   - Convention tests that scan the source code for forbidden APIs (e.g. `IgnoreQueryFilters`).
   - Playwright tests in the real browser.
7. **"Security?"** Password hashing and lockout (Identity), antiforgery token on every write,
   SameSite=Strict cookies, rate limiting, `no-store` caching, safe file storage (GUID names, file
   signature check, outside `wwwroot`). Documented in `docs/security.md`, including the known gaps.
8. **"How does the demo work without risk for real data?"** Each visitor gets an account without a password,
   isolated by the same filters. It is deleted after 4 hours. Two startup safeguards refuse to run the demo on the
   default data folder or on a database with real accounts.
9. **"What would you do differently / next?"** E-mail-based password reset, possibly PostgreSQL for
   several users and more than one server instance.
10. **"Did you use AI?"** Yes, Claude Code. Be ready to explain *how* you used it (planning,
    reviewing, testing) and to walk through the central files yourself:
    - `Program.cs`
    - `AppDbContext.cs`
    - `ApplicationService.cs`
    - one endpoint file
    - one test file

11. **"How is the demo hosted?"** One container on Fly.io with a volume for SQLite, HTTPS from Fly's
    proxy, deployed manually through GitHub Actions; see `Dockerfile`, `fly.toml` and
    `docs/deployment.md`.

Practice suggestion: explain the request path for "change status" from the browser to the database
without looking at the code. Then explain why `CurrentUserId` must be a property and not a local
variable.
