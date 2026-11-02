# TrustVault

TrustVault is a secure document-sharing platform. This repository starts the
Sprint 0 foundation; document workflows, login/token issuance, and audit writing
are not implemented yet.

## Architecture

- `frontend/`: Angular 21 and TypeScript.
- `backend/TrustVault.Api`: ASP.NET Core 8 API, JWT validation, and deny-by-default
  authentication policy.
- `backend/TrustVault.Application`: application use cases (to be implemented).
- `backend/TrustVault.Domain`: document, grant, version, audit, and role types.
- `backend/TrustVault.Infrastructure`: ASP.NET Core Identity and PostgreSQL EF Core
  persistence.
- `backend/TrustVault.Tests`: API authorization smoke tests.
- PostgreSQL 16 is provided locally by Docker Compose.

See [docs/architecture.md](docs/architecture.md), [docs/api.md](docs/api.md),
[docs/backlog.md](docs/backlog.md), and [THREATS.md](THREATS.md).

## Requirements

- .NET 8 SDK
- Node.js 20.19+ or 22.12+ and npm
- Docker Desktop with Docker Compose

## Run locally

From the repository root in PowerShell:

```powershell
docker compose up -d postgres
$env:ConnectionStrings__TrustVault = "Host=localhost;Port=5432;Database=trustvault;Username=trustvault_app;Password=local_only"
$keyBytes = New-Object byte[] 32
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($keyBytes)
$rng.Dispose()
$env:Jwt__SigningKey = [Convert]::ToBase64String($keyBytes)
$env:Jwt__Issuer = "TrustVault"
$env:Jwt__Audience = "TrustVault"
$env:Frontend__Origin = "http://localhost:4200"
dotnet tool restore
dotnet ef database update --project backend\TrustVault.Infrastructure --startup-project backend\TrustVault.Api
dotnet run --project backend\TrustVault.Api
```

In another PowerShell window:

```powershell
Set-Location frontend
npm ci
npm start
```

The API listens at `http://localhost:5106` and the Angular app at
`http://localhost:4200`. `GET /api/system/health` is anonymous; other routes
require a valid JWT. No login/token-issuing endpoint exists in Sprint 0.
The development API documentation is at `/swagger`.

## Database

The initial EF Core migration is in
`backend/TrustVault.Infrastructure/Persistence/Migrations`. The local run steps
apply it to the Compose database before starting the API.

The design-time context uses the configured connection string or the local
Compose default. The Compose credentials are for local development only.

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

CI runs the backend format/build/test/migration checks and the frontend lint,
test, production build, and production dependency audit. Sprint 0 does not
include an Azure deployment, public URL, seeded demo users, or demo video.

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
