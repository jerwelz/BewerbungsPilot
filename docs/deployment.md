# Deployment assessment (demo mode)

Status: **not deployed.** This document records the assessment made in M10. Nothing has been
set up or paid for.

## What a public demo instance needs

| Requirement | Why | Already in the code? |
|---|---|---|
| ASP.NET Core 10 runtime | Target framework `net10.0` | – (host must support it, or use a container) |
| Persistent disk for one folder | SQLite file, demo PDFs and Data Protection keys live in the data folder | `Storage:DataDirectory` is configurable |
| Exactly **one** instance | SQLite and the per-process rate limiter do not work across several instances | – (host setting) |
| HTTPS | `__Host-` cookies and `Secure` are enforced outside Development | HSTS + HTTPS redirect on |
| Correct client IP behind a proxy | Rate limiting per IP | `ReverseProxy:KnownProxies` |
| Host name | `AllowedHosts` is localhost-only by default | config value |
| Demo configuration | `Demo:Enabled=true`, `Storage:DataDirectory=<not storage>`, `ASPNETCORE_ENVIRONMENT=Production` | demo guards refuse unsafe setups |
| Reset mechanism | Visitors must not fill the disk | per-visitor accounts, deleted after 4 h, max 200 at a time, uploads off |

The demo mode needs **no separate reset job**: the data of each visitor disappears with the
account. Restarting the instance with an empty data folder resets everything.

## Options

Prices are rough figures from third-party summaries (October 2026). Check them on the provider's
own pricing page before deciding.

| Option | Fit | Persistence | Cost (approx.) | Effort / risk |
|---|---|---|---|---|
| **Azure App Service, Free F1** | Native .NET hosting, HTTPS on `*.azurewebsites.net` | `/home` (`D:\home`) is persistent | free | Dev/test tier with daily CPU quota and 32-bit only. Whether .NET 10 is offered on F1 must be checked. The app may stop when the quota is used up. |
| **Azure App Service, Basic B1** | Same, without the quota problem | persistent | about €12–15/month | low effort, publish from Visual Studio / `dotnet publish` |
| **Fly.io** (container + volume) | One small machine, volume for the data folder | volume ($0.15/GB/month) | about $2–5/month; no free tier for new accounts | Needs a Dockerfile and the `fly` CLI. Data Protection keys are unencrypted on Linux, so the volume must stay private. |
| **Render** (container) | Free web services sleep after ~15 min idle (cold start) | persistent disks only on paid instances | about $7/month + disk | Needs a Dockerfile. The free tier is unusable because it has no persistent disk. |
| **Small VPS** (e.g. 2 GB) + Caddy (automatic HTTPS) | Full control | local disk | about €4–6/month | Needs Linux administration: updates, firewall, backups, systemd. This is the most operations work. |

## Recommendation

1. **Postpone the public deployment.** For applications, the repository with README, screenshots
   and a two-command local demo is enough. Every public instance needs ongoing maintenance (updates,
   monitoring, cost) and creates a public attack surface.
2. If a live demo becomes important later, use **one small container with one volume, running demo
   mode only** (Fly.io or Azure App Service B1). This needs:
   - a `Dockerfile` (multi-stage `dotnet publish`, non-root user, data folder on the volume)
   - environment variables: `ASPNETCORE_ENVIRONMENT=Production`, `Demo__Enabled=true`,
     `Storage__DataDirectory=/data/demo`, `AllowedHosts=<host>`, and `ReverseProxy__KnownProxies` if the
     proxy address is known
   - the checklist in [security.md §8](security.md#8-before-exposing-bewerbungspilot-to-the-internet)
   - recommended: a Content-Security-Policy header and monitoring of the 429 responses
3. **Never** deploy the private instance or copy `storage/` to a server.

Sources (third-party summaries, not official price lists):
[Azure free tier guide](https://agentdeals.dev/azure-free-tier-2026) ·
[Microsoft Q&A: F1 32-bit](https://learn.microsoft.com/en-us/answers/questions/541687/i-am-using-f1-tier-for-app-service-and-i-can-only) ·
[Render pricing 2026](https://temps.sh/blog/render-pricing-2026) ·
[Fly.io pricing 2026](https://temps.sh/blog/fly-io-pricing-2026)
