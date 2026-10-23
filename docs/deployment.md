# Deployment of the public demo (Fly.io)

**Status: prepared, not deployed.** M11 contains everything needed (`Dockerfile`, `fly.toml`,
`.github/workflows/deploy.yml`, production safeguards and tests). A Fly.io account and its first
deployment have to be set up by the owner of the repository (steps below). The public URL is only
called "live" once it has been tested.

The public instance runs **demo mode only**: every visitor gets an own throwaway account with
fictional data. It never contains real applications – the personal `storage/` folder is never
copied, built into the image or uploaded.

```text
Visitor ──HTTPS──▶ Fly.io proxy (TLS, bewerbungspilot.fly.dev)
                        │  plain HTTP, X-Forwarded-Proto, Fly-Client-IP
                        ▼
                 1 machine: container from ./Dockerfile (ASP.NET Core 10, user "app")
                        │
                        ▼
                 Fly volume /data  →  /data/demo/  bewerbungspilot.db (SQLite, WAL)
                                                   documents/ (generated demo PDFs)
                                                   keys/ (Data Protection keys)
```

## Choice of provider

Budget: €0–5 per month. Prices from the providers' official pages, checked on 2026-10-23.

| Option | Persistent disk for SQLite | Cost | Other points | Verdict |
|---|---|---|---|---|
| **Fly.io** (container + volume) | yes, volume (local disk of the machine) | shared-cpu-1x 512 MB: **$3.69/month** if always running, billed per second; volume **$0.15 per GB/month**; shared IPv4, IPv6 and the first 10 certificates free | No free tier for new accounts (trial: 2 h machine time or 7 days); **credit card required**. HTTPS on `*.fly.dev`, custom domains possible. Deploy with `flyctl` or GitHub Actions. | **chosen** |
| Render, free web service | **no** – only paid services can attach a disk; file system lost on restart/redeploy/spin-down | $0 | Sleeps after 15 min without requests, ~1 min to wake up – a bad first impression for recruiters; every sleep would delete all demo accounts and log visitors out | not suitable |
| Azure App Service, Free F1 | `/home` is persistent but network storage (not verified here); SQLite's WAL mode needs a local file system | $0 | 60 CPU minutes per day, 1 GB RAM, no SLA, "not supported for production workloads" (official page); a card is needed for an Azure account (except special student offers) | not suitable |
| Azure App Service, Basic B1 | as above | price not shown on the page at the time of checking (calculator only); third-party summaries name €12–15/month | above the budget | not chosen |
| Small VPS + Caddy | local disk | about €4–6/month (not verified) | Linux administration: updates, firewall, backups | more work than needed |

**Why Fly.io:** the only option in budget with a real persistent disk, HTTPS out of the box and a
Docker deployment that also documents itself. With `auto_stop_machines = "stop"` the machine stops
when nobody uses the demo and starts on the next request (a few seconds of delay). Expected cost:
the volume ($0.15) plus the running time of the machine – at most $3.84/month if it never stops.

Sources: [Fly.io pricing](https://fly.io/docs/about/pricing/) ·
[Fly.io configuration](https://fly.io/docs/reference/configuration/) ·
[Fly.io request headers](https://fly.io/docs/networking/request-headers/) ·
[Fly.io volume snapshots](https://fly.io/docs/volumes/snapshots/) ·
[Render free instances](https://render.com/docs/free) ·
[Azure App Service Linux pricing](https://azure.microsoft.com/pricing/details/app-service/linux/) ·
[SQLite WAL and network file systems](https://www.sqlite.org/wal.html)

## Configuration

All values are in `fly.toml` (`[env]`) or the `Dockerfile`. No secrets are needed: there is no
e-mail, no external API key, and the Data Protection keys are created on the volume at first start.

| Variable | Value | Purpose |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Secure `__Host-` cookies, HSTS, HTTPS redirect, no error details |
| `Demo__Enabled` | `true` | demo mode: start page with "Demo starten", registration off, uploads off |
| `Storage__DataDirectory` | `/data/demo` | data folder on the volume (must be absolute – safeguard) |
| `AllowedHosts` | `bewerbungspilot.fly.dev` | only this host name is answered (must not be `*` – safeguard) |
| `ASPNETCORE_HTTPS_PORT` | `443` | target port of the HTTPS redirect (the app itself listens on HTTP 8080) |
| `ReverseProxy__TrustAllProxies` | `true` | accept `X-Forwarded-*` – port 8080 is reachable only through Fly's proxy |
| `ReverseProxy__ClientIpHeader` | `Fly-Client-IP` | the visitor's address for the rate limits |
| `ASPNETCORE_HTTP_PORTS` | `8080` (Dockerfile) | Kestrel port |
| `DOTNET_gcServer` | `0` (Dockerfile) | workstation GC: less memory on a 512 MB machine |
| optional `Demo__AccountLifetime` | default `04:00:00` | lifetime of a demo account |
| optional `Demo__MaxAccounts` | default `200` | simultaneous demo accounts |
| optional `Demo__StartsPerHour` | default `5` | demo starts per visitor IP and hour |
| optional `Demo__WritesPerMinute` | default `60` | changing requests per visitor and minute |

If the app name `bewerbungspilot` is already taken on Fly.io, choose another name and change it in
`fly.toml` (`app`, `AllowedHosts`, health-check `Host`) and in `.github/workflows/deploy.yml` (URL).

## One-time setup (owner, needs approval and a credit card)

1. Create a Fly.io account and add a payment method: <https://fly.io/app/sign-up>.
2. Install `flyctl` ([instructions](https://fly.io/docs/flyctl/install/)) and log in: `fly auth login`.
3. In the repository folder:
   ```bash
   fly apps create bewerbungspilot
   fly volumes create bewerbungspilot_data --app bewerbungspilot --region fra --size 1
   fly tokens create deploy --app bewerbungspilot      # prints a token
   gh secret set FLY_API_TOKEN --repo jerwelz/BewerbungsPilot   # paste the token
   ```
4. GitHub → Actions → **Deploy demo (Fly.io)** → *Run workflow*. The workflow builds the image
   from a clean checkout on Fly's builder, deploys it and checks `https://bewerbungspilot.fly.dev/health`.

Do **not** run `fly launch` (it would rewrite `fly.toml`). Avoid `fly deploy` from a local working
copy; if it is used anyway, `.dockerignore` (an allow-list) keeps `storage/` out of the build context.

## After the first deployment: verification

Open `https://bewerbungspilot.fly.dev` and check:

1. The start page appears; "Demo starten" leads to the dashboard with the demo banner.
2. All seven pages (Übersicht, Bewerbungen, Pipeline, Kalender, Verlauf, Analyse, Standorte) work,
   a PDF opens in the preview, the map shows tiles.
3. `http://bewerbungspilot.fly.dev` redirects to HTTPS; `/health` answers `Healthy`.
4. Response headers contain `Strict-Transport-Security` and `Content-Security-Policy`; cookies start with `__Host-`.
5. `fly logs` shows "Demo-Konto erstellt" and the TrustAllProxies warning, no errors.
6. Rate limit check: six "Demo starten" from the same browser within an hour → the sixth gets the
   German "zu viele Demos" message (shows that `Fly-Client-IP` is used, not the proxy address).

## Operation

- **Restart behaviour:** Fly restarts the machine when the process exits (`[[restart]]
  policy = "on-failure"`). The health check `/health` (database reachable) runs every 30 s; a
  failing check takes the machine out of the proxy's rotation. Auto-stop stops the machine when idle;
  the next request starts it again. Data and logins survive restarts and redeploys (volume).
- **Cleanup:** expired demo accounts are deleted at every start and every 10 minutes.
- **Monitoring:** `fly status`, `fly logs`, the metrics page in the Fly dashboard (memory, requests,
  response times), volume usage via `fly volumes list`. Optional: an external uptime check of
  `/health` (note that it keeps the machine awake, which increases cost).
- **Backup and recovery:** the demo contains only disposable data. Fly takes a daily snapshot of the
  volume (kept 5 days). Recovery: redeploy; if the volume is damaged, create a new one (empty, or
  from a snapshot with `fly volumes create … --snapshot-id <id>`). An empty volume simply starts a
  fresh demo database.
- **Updates:** push to `main` → CI (including the container job) must be green → run the deploy
  workflow manually.
- **Costs:** check the billing page in the Fly dashboard after the first days.
- **Switching off:** `fly scale count 0 --app bewerbungspilot` (stops) or `fly apps destroy
  bewerbungspilot` (deletes app and volume).

## Never

- Never deploy without `Demo__Enabled=true`, and never with a data folder that contains real data.
- Never copy `storage/` (database, documents, keys, backups) to a server or into an image.
- Never set `ReverseProxy__TrustAllProxies=true` on a host where port 8080 is publicly reachable.
