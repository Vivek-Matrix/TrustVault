# TrustVault Setup Guide

Covers prerequisites, running locally, configuration, and deployment. Commands
are taken from the repository `README.md`, `docker-compose.yml`,
`.github/workflows/ci.yml`, and `frontend/package.json`. Nothing here is
invented; anything unverified is marked **Needs confirmation**.

## Table of contents

1. [Prerequisites](#1-prerequisites)
2. [Get the code](#2-get-the-code)
3. [Run locally (PowerShell)](#3-run-locally-powershell)
4. [First sign-in and smoke test](#4-first-sign-in-and-smoke-test)
5. [Configuration reference](#5-configuration-reference)
6. [Local ports and services](#6-local-ports-and-services)
7. [Troubleshooting](#7-troubleshooting)
8. [Deployment on Railway](#8-deployment-on-railway)
9. [Gaps](#9-gaps)

---

## 1. Prerequisites

| Tool | Version | Evidence |
|---|---|---|
| .NET SDK | 10 | all `.csproj` target `net10.0`; `ci.yml` uses `10.0.x` |
| Node.js + npm | 20.19+ or 22.12+ | `README.md`; CI uses Node 20, `Dockerfile` uses `node:22-alpine` |
| Docker Desktop + Compose | recent | `docker-compose.yml` |
| `dotnet-ef` | 10.0.14 (local tool) | `.config/dotnet-tools.json`, restored by `dotnet tool restore` |
| Chrome / Chromium | any | frontend tests run with `ChromeHeadless` |

The documented shell is **PowerShell**. A bash translation is in
[§3.1](#31-bash-equivalent-untested).

## 2. Get the code

```bash
git clone https://github.com/Vivek-Matrix/TrustVault.git
cd TrustVault
git checkout feature/TV-001-project-foundation
```

The branch name comes from the owner's Railway notes; the uploaded snapshot is
named `TrustVault-feature-TV-001-project-foundation`.

## 3. Run locally (PowerShell)

Run from the repository root. This is the sequence from `README.md`.

```powershell
# 1. Start PostgreSQL, ClamAV, converter and its dev proxy
docker compose up -d --build --force-recreate postgres clamav converter converter-dev-proxy

# 2. Create restricted DB roles (local-only passwords; never reuse elsewhere)
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql

# 3. Runtime configuration
$env:ConnectionStrings__TrustVault = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_runtime;Password=runtime_local"
$keyBytes = New-Object byte[] 32
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($keyBytes); $rng.Dispose()
$env:Jwt__SigningKey = [Convert]::ToBase64String($keyBytes)
$env:Jwt__Issuer = "TrustVault"
$env:Jwt__Audience = "TrustVault"
$env:AdminBootstrap__Email = "admin@gmail.com"
$secureAdminPassword = Read-Host "Set the bootstrap administrator password" -AsSecureString
$env:AdminBootstrap__Password = [System.Net.NetworkCredential]::new("", $secureAdminPassword).Password
$env:Frontend__Origin = "http://localhost:4200"
$env:DocumentConversion__Endpoint = "http://localhost:6100/"
$env:ConnectionStrings__TrustVaultMigrator = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_migrator;Password=migrator_local"

# 4. Migrate, re-apply role grants, drop migrator credential, start the API
dotnet tool restore
dotnet run --project backend\TrustVault.Migrator
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql
Remove-Item Env:ConnectionStrings__TrustVaultMigrator
dotnet run --project backend\TrustVault.Api
```

Why the role script runs twice: the first run creates roles before tables
exist; after migration, the second run applies table grants and the
SELECT/INSERT-only restriction on `audit_events` (`database/setup-roles.sql`,
`ci.yml` steps "Provision restricted database roles" and "Enforce append-only
audit privileges").

In a second terminal:

```powershell
Set-Location frontend
npm ci
npm start
```

The API listens on `http://localhost:5106` (`launchSettings.json`), the SPA on
`http://localhost:4200`. Swagger UI is at `/swagger` in Development only.

Optional: set `Storage__RootPath` to relocate stored documents; the default is
`App_Data/documents` under the API content root (`Program.cs`).

### 3.1 Bash equivalent (untested)

**Needs confirmation:** a direct translation of the PowerShell steps; the repo
only documents PowerShell.

```bash
docker compose up -d --build --force-recreate postgres clamav converter converter-dev-proxy
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql
export ConnectionStrings__TrustVault="Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_runtime;Password=runtime_local"
export Jwt__SigningKey="$(openssl rand -base64 32)"
export Jwt__Issuer=TrustVault Jwt__Audience=TrustVault
export AdminBootstrap__Email="admin@gmail.com"
read -rsp "Bootstrap administrator password: " AdminBootstrap__Password; export AdminBootstrap__Password
export Frontend__Origin="http://localhost:4200"
export DocumentConversion__Endpoint="http://localhost:6100/"
export ConnectionStrings__TrustVaultMigrator="Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_migrator;Password=migrator_local"
dotnet tool restore
dotnet run --project backend/TrustVault.Migrator
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql
unset ConnectionStrings__TrustVaultMigrator
dotnet run --project backend/TrustVault.Api
```

## 4. First sign-in and smoke test

1. Open `http://localhost:4200`, sign in with `AdminBootstrap__Email` and its password.
2. **Manage members** → add a member by email. Copy the one-time temporary
   password and deliver it out-of-band.
3. Sign out; sign in as the member and change the password (required).
4. **Documents** → upload a small dummy PDF/TXT/DOC/DOCX (≤ 10 MiB). Wait for
   scan status `clean`. If it shows `scan failed`, confirm ClamAV is ready and
   choose **Retry scan**.
5. Create a second member, then **Manage access** → grant READ, sign in as the
   second member, confirm the document appears; revoke and confirm it disappears.

6. Give the second member **Edit** access and try: save a new title, edit TXT
   or Word text, edit a disposable PDF (add text, draw, replace area,
   reorder/remove pages), and replace the PDF. The earlier file should appear
   under **Previous versions**.
7. Use a disposable PDF to test **Redact area**; after saving, the covered area
   is black and the old file no longer appears in previous versions. This does
   not erase backups or copies already downloaded.
8. Click the clock icon to view creation/read/edit history (owners and active
   grantees only).
9. As owner, use the trash icon to delete a document; this revokes all shared
   access and makes previous versions unavailable.

(Steps 6–9 summarise the earlier README walkthrough; behaviour is implemented
in `DocumentsController.cs`.)

> Do not upload real confidential files. Local storage and scanning are
> development configurations.

## 5. Configuration reference

.NET maps `:` to `__` in environment variables. Defaults below are from the
code lines cited.

| Key | Default | Required | Source / behaviour |
|---|---|---|---|
| `ConnectionStrings:TrustVault` | none | **Yes** (API) | `DatabaseConnectionStrings.Get`; `postgres://` / `postgresql://` URLs are normalised |
| `ConnectionStrings:TrustVaultMigrator` | none | Migrator, `dotnet-ef` | `Migrator/Program.cs`, `ApplicationDbContextFactory.cs`; API never falls back to it |
| `Jwt:SigningKey` | none | **Yes** | `Program.cs`: ≥ 32 UTF-8 bytes or startup fails |
| `Jwt:Issuer` / `Jwt:Audience` | `TrustVault` | No | `Program.cs` |
| `Jwt:AccessTokenLifetimeMinutes` | 15 | No | Must be 5–30 (`Program.cs`) |
| `Jwt:RefreshTokenLifetimeDays` | **Needs confirmation** | No | `AuthenticationService.cs` (default line not inspected) |
| `AdminBootstrap:Email` / `:Password` | none | First start | `AdministratorBootstrapper.cs`; may be removed once the admin exists |
| `Frontend:Origin` | `http://localhost:4200` | Production: yes | CORS (`Program.cs`) |
| `DocumentConversion:Endpoint` | `http://localhost:6100/` | For Word editing | `DocumentTextConverter.cs` |
| `Storage:RootPath` | `<content root>/App_Data/documents` | No | `Program.cs` |
| `MalwareScanning:ClamAv:Host` | `127.0.0.1` | **Non-Development: yes** | Must not be localhost/loopback/any address outside Development |
| `MalwareScanning:ClamAv:Port` | 3310 | No | 1–65535 |
| `MalwareScanning:ClamAv:TimeoutSeconds` | 45 | No | 1–300 |
| `RateLimits:AuthenticationPermitLimit` | 10 / min / IP+path | No | Raised in the test host |
| `PORT` | unset | No | If numeric, API binds `http://0.0.0.0:$PORT` (`Program.cs`; variable is named `renderPort` in code) |
| `ASPNETCORE_HTTP_PORTS` | 10000 | No | Set in root `Dockerfile` |
| `TRUSTVAULT_TEST_CONNECTION` | local default | Tests | `ApiAuthorizationTests.cs`; see [TESTING.md](TESTING.md) |
| Frontend `apiBaseUrl` | dev `http://localhost:5106/api`, prod `/api` | n/a | `frontend/src/environments/*.ts` |

Never commit secrets: `.env`, `.env.*` are git-ignored (`.gitignore`).

## 6. Local ports and services

| Service | Address | Notes (`docker-compose.yml`) |
|---|---|---|
| PostgreSQL 16 | `localhost:55432` | Named volume `trustvault-postgres`; do **not** run `docker compose down -v` |
| ClamAV | `127.0.0.1:3310` | `clamav/clamav:1.4.6`, 2 GB limit, 1 CPU; first start downloads signatures and can take minutes |
| Converter | internal only | Read-only FS, `cap_drop: ALL`, 768 MB |
| Converter dev proxy | `127.0.0.1:6100` | Nginx relay for local use only |
| API | `localhost:5106` | |
| SPA dev server | `localhost:4200` | |

## 7. Troubleshooting

| Symptom | Check |
|---|---|
| Migrator error `28P01` | Confirm Compose runs from this folder, then reset the local-only password and test the TCP port (below). Only for local Compose credentials. |
| Upload stuck in `pending`/`scan failed` | ClamAV is still loading signatures or is down; `GET /api/system/ready` must return `ready`; then **Retry scan** |
| Word editing fails | Converter not running or `DocumentConversion__Endpoint` wrong |
| API exits on start | `Jwt__SigningKey` < 32 bytes, or missing connection string, or non-Development ClamAV host is loopback |
| `npm ci` file-lock errors on Windows | Stop the dev server first (noted in `PROMPTS.md`) |

```powershell
docker compose exec -T postgres psql -U trustvault_app -d trustvault -c "ALTER ROLE trustvault_app WITH PASSWORD 'local_only';"
docker run --rm --add-host=host.docker.internal:host-gateway -e PGPASSWORD=local_only postgres:16-alpine psql -h host.docker.internal -p 55432 -U trustvault_app -d trustvault -c "select current_user, current_database();"
```

Only rerun `dotnet run --project backend\TrustVault.Migrator` once the TCP test succeeds.

## 8. Deployment on Railway

**Needs confirmation for this whole section:** the repository contains no
Railway configuration. The facts below come from the owner's inspection of the
Railway project and were not verified against the live service.

| Item | Value |
|---|---|
| Project / environment | `daring-strength` / production |
| Services | `TrustVault`, `converter`, `Postgres`, `clamav` |
| Source | GitHub `Vivek-Matrix/TrustVault`, branch `feature/TV-001-project-foundation` (TrustVault and converter) |
| TrustVault build | Root `Dockerfile` (multi-stage: Angular → .NET publish of API + migrator → runtime) |
| Pre-deploy step | `TrustVault.Migrator.dll`, 60 s limit. The Dockerfile copies the migrator to `/app/migrator/` (exact command **Needs confirmation**) |
| App port / healthcheck | 8080; `GET /api/system/ready` for up to 60 s |
| Postgres | Railway Postgres template |
| clamav | `clamav/clamav:1.4.6`, 500 MB volume at `/var/lib/clamav` |
| App → ClamAV | `MalwareScanning__ClamAv__Host` / `__Port` variables |
| Converter | Built from `backend/TrustVault.Converter/Dockerfile` (listens on 8080) |

Required service variables (from §5): `ConnectionStrings__TrustVault`,
`ConnectionStrings__TrustVaultMigrator`, `Jwt__SigningKey`,
`AdminBootstrap__*`, `Frontend__Origin`, `DocumentConversion__Endpoint`,
`MalwareScanning__ClamAv__Host`/`Port`.

Operational notes:

- `/api/system/ready` depends on ClamAV (`SystemController.Ready`). If `clamav`
  is down, the TrustVault deploy is marked unhealthy even though the process is
  running. Deploy/repair `clamav` **before** `TrustVault`.
- Production roles: the SQL script uses local-only passwords. Whether separate
  runtime/migrator roles exist on Railway is **Needs confirmation**.
- Document storage uses the container filesystem unless a volume is mounted at
  `Storage__RootPath` (**Needs confirmation**).
- CI does not deploy. Any auto-redeploy comes from Railway watching the branch
  (**Needs confirmation**; `ci.yml` has no deploy job).

### Current minor infrastructure limitation: `clamav` memory

Symptom (owner's logs): `clamav` restarts in a loop; signatures load
(`main.cvd` ≈ 3.29 M, `daily.cvd` ≈ 356 K) but `clamd` never starts, so
readiness returns 503.

Probable cause (**Needs confirmation**, the actual `clamd` error is not in the
logs): ClamAV needs roughly 1.5–2 GB RAM for ~3.6 M signatures, and the 500 MB
volume leaves little room for updates. This is a plan-size/billing limit, not a
code defect; the paid tier could not be activated yet.

Simple fix:

1. Move to a Railway plan with enough memory (billing method required).
2. Set the `clamav` memory limit to about 2 GB (the local Compose uses `mem_limit: 2g`).
3. Enlarge `clamav-volume` beyond 500 MB.
4. Redeploy `clamav`; confirm `clamd` starts.
5. Redeploy `TrustVault`; confirm `/api/system/ready` returns `{"status":"ready"}`.

There is no configuration option to bypass scanning, and that is deliberate:
the project keeps malware scanning on rather than weakening the security model.

## 9. Gaps

- No Railway config files, no `.env.example` (despite the `.gitignore` exception).
- `docs/deployment.md` still describes AWS; it is superseded by this section.
- Production durable storage and DB-role provisioning are unverified.
