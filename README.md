# TrustVault

TrustVault is a secure document-sharing platform. This repository starts the
Sprint 0 foundation; document workflows, login/token issuance, and audit writing
are not implemented yet.

## Architecture

- `frontend/`: Angular 21 and TypeScript.
- `backend/TrustVault.Api`: ASP.NET Core 10 API, JWT validation, and deny-by-default
  authentication policy.
- `backend/TrustVault.Application`: application use cases (to be implemented).
- `backend/TrustVault.Domain`: document, grant, version, audit, and role types.
- `backend/TrustVault.Infrastructure`: ASP.NET Core Identity and PostgreSQL EF Core
  persistence.
- `backend/TrustVault.Migrator`: one-shot EF Core database migration runner for
  controlled deployment release commands.
- `backend/TrustVault.Tests`: API authorization smoke tests.
- PostgreSQL 16 is provided locally by Docker Compose.

## Hosting direction

The selected prototype hosting direction is Render for the ASP.NET Core API,
Angular frontend, and managed PostgreSQL, with Cloudflare R2 for private document
objects. GitHub Actions will run CI and later deploy from protected branches.
This is a hosting decision, not a claim that deployment or document workflows are
ready. Do not upload real confidential files until server-side document
authorization, immediate revocation, secure storage, and audit behavior are
implemented and tested.

See [deployment plan](docs/deployment.md) before creating cloud resources.

See [docs/architecture.md](docs/architecture.md), [docs/api.md](docs/api.md),
[docs/backlog.md](docs/backlog.md), and [THREATS.md](THREATS.md).

## Requirements

- .NET 10 SDK
- Node.js 20.19+ or 22.12+ and npm
- Docker Desktop with Docker Compose

## Run locally

From the repository root in PowerShell:

```powershell
docker compose up -d --force-recreate postgres
$env:ConnectionStrings__TrustVault = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_app;Password=local_only"
$keyBytes = New-Object byte[] 32
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($keyBytes)
$rng.Dispose()
$env:Jwt__SigningKey = [Convert]::ToBase64String($keyBytes)
$env:Jwt__Issuer = "TrustVault"
$env:Jwt__Audience = "TrustVault"
$env:Frontend__Origin = "http://localhost:4200"
dotnet tool restore
dotnet run --project backend\TrustVault.Migrator
dotnet run --project backend\TrustVault.Api
```

In another PowerShell window:

```powershell
Set-Location frontend
npm ci
npm start
```

The API listens at `http://localhost:5106` and the Angular app at
`http://localhost:4200`. `GET /api/system/health` is anonymous; the readiness
endpoint `GET /api/system/ready` also checks database connectivity. Other API
routes require a valid JWT. No login/token-issuing endpoint exists in Sprint 0.
The development API documentation is at `/swagger`.

## Database

The initial EF Core migration is in
`backend/TrustVault.Infrastructure/Persistence/Migrations`. The local run steps
apply it to the Compose database before starting the API.

The design-time context uses the configured connection string or the local
Compose default. Compose publishes PostgreSQL on host port `55432` to avoid
collisions with another local PostgreSQL instance; container port remains `5432`.
The Compose credentials are for local development only. If the database
container existed before the host-port change, the command above recreates the
container while preserving its named volume. Do not use `docker compose down -v`;
that would delete the local database volume.

If the migrator reports PostgreSQL error `28P01`, first confirm that Compose is
running from this repository folder. Then reset the local-only role password and
test the published TCP port:

```powershell
docker compose exec -T postgres psql -U trustvault_app -d trustvault -c "ALTER ROLE trustvault_app WITH PASSWORD 'local_only';"
docker run --rm --add-host=host.docker.internal:host-gateway -e PGPASSWORD=local_only postgres:16-alpine psql -h host.docker.internal -p 55432 -U trustvault_app -d trustvault -c "select current_user, current_database();"
```

Only continue when the TCP test succeeds. Then rerun
`dotnet run --project backend\TrustVault.Migrator`. These commands apply only to
the local Compose database and its non-production credentials.

## Verify

```powershell
dotnet test TrustVault.sln
Set-Location frontend
npm ci
npm run lint
npm test -- --watch=false --browsers=ChromeHeadless
npm run build
npm audit --omit=dev
```

CI runs the backend format/build/test/migration checks, a production Docker image
build, and frontend lint/test/build/dependency audit. Render resources and a
public URL still require manual provider setup. No seeded demo users or demo
video are included.

## Security status

The API validates JWT signatures, issuer, audience, and expiration, and applies an
authenticated-user fallback policy. The document permission policy handlers,
login and refresh flows, upload validation, storage integration, audit hash
chaining, and production DB-role split are follow-up work. An endpoint is not
considered secure merely because its UI is hidden.

## First commit plan

`feat(sprint-0): establish TrustVault project foundation` — solution/workspace,
initial data model and migration, auth boundary smoke tests, local development
setup, CI, and architecture/security notes. Keep later user stories in separate
feature branches and commits.
