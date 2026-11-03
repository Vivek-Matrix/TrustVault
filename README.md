# TrustVault

TrustVault is a secure document-sharing platform. Sprint 1 authentication is
implemented, and Sprint 2 adds an owner-only document API slice with private
local PDF storage and audit events. Sharing, refresh-token issuance, the
production deployment, and named-user sharing are not implemented yet.

## Architecture

- `frontend/`: Angular 21 and TypeScript.
- `backend/TrustVault.Api`: ASP.NET Core 10 API, Identity registration/login,
  short-lived JWT validation, deny-by-default authorization policy, and
  owner-authorized document endpoints.
- `backend/TrustVault.Application`: application use cases and private document
  storage contract.
- `backend/TrustVault.Domain`: document, grant, version, audit, and role types.
- `backend/TrustVault.Infrastructure`: ASP.NET Core Identity and PostgreSQL EF Core
  persistence.
- `backend/TrustVault.Migrator`: one-shot EF Core database migration runner for
  controlled deployment release commands.
- `backend/TrustVault.Tests`: API authentication and document authorization
  integration tests.
- PostgreSQL 16 is provided locally by Docker Compose.

## Hosting direction

AWS is the selected cloud provider for a future internet deployment. The AWS
experience guidance is present in `.github/copilot-instructions.md`. AWS CLI
v2 and `uv` are available locally, and the `trustvault-dev` profile has been
authenticated for `ap-southeast-2`. The existing workspace AWS MCP entry uses
a remote HTTP endpoint; the local Agent Toolkit wizard was left unrun to avoid
overwriting that connection. TrustVault is not deployed to AWS and no AWS
application or database resources have been created. The owner reports $100 in
AWS credits and confirmed the selected Region is `ap-southeast-2`; credits are
not a monthly spend cap. Confirm the project spend limit, service eligibility,
and a complete cost estimate before provisioning anything. The current Docker
image is built by CI; it is not published to AWS.

The earlier Render deployment instructions are superseded. See the
[AWS deployment status and prerequisites](docs/deployment.md) before taking
deployment steps. Do not upload real confidential files until server-side
named-user authorization, immediate revocation, production-grade storage, and
audit integrity controls are implemented and tested.

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
# Optional: documents are stored outside wwwroot at this path by default.
# $env:Storage__RootPath = "C:\TrustVaultData\documents"
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
`http://localhost:4200`. Health and the registration/login endpoints are
anonymous; the other API routes require a valid JWT. New accounts receive the
Viewer role. Authentication returns a 15-minute bearer token by default,
configurable to 5-30 minutes with `Jwt:AccessTokenLifetimeMinutes`. The Angular
client keeps the token in memory only, so closing or reloading the page signs
the user out. The development API documentation is at `/swagger`.

After signing in, the Angular screen lets you upload, list, refresh, and
download your own PDFs. Uploads are limited to 10 MiB. The server checks the
file extension and `%PDF-` signature, then stores it under an opaque generated
key outside the static web root. The current filesystem provider is for local
development only. Use dummy files, not confidential documents.

### Try the document screen

1. Keep the API running in the first PowerShell window.
2. In a second PowerShell window, run `Set-Location frontend` and then
   `npm start`.
3. Open `http://localhost:4200`, create an account, and sign in.
4. In **Documents**, enter a title/category, choose a small dummy PDF, and
   select **Upload PDF**. The uploaded file appears in your list; use
   **Download** to save it.
5. Sign out, create a second test account, and confirm its document list is
   empty. Automated API tests separately verify that a different account
   cannot retrieve another owner's document, even with its ID.

Do not use real confidential files. The browser keeps the access token only in
memory; refreshing or closing the page signs the user out. The current UI does
not provide sharing or permission management.

## Database

The initial EF Core migration is in
`backend/TrustVault.Infrastructure/Persistence/Migrations`. The local run steps
apply it to the Compose database before starting the API. The migrator has been
verified against both the existing local Compose database and a separate empty
PostgreSQL database. The existing named volume was preserved.

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
build, and frontend lint/test/build/dependency audit. AWS resources and a public
URL are not configured. No seeded demo users or demo video are included.

## Security status

The API validates JWT signatures, issuer, audience, and expiration, and applies
an authenticated-user fallback policy. Registration assigns the least-privilege
Viewer role; passwords use Identity's configured requirements and hashing.
Failed sign-ins trigger Identity lockout, and authentication routes have an
in-memory per-IP rate limit. Document metadata and downloads are restricted to
the owner on every request; unauthorized users receive 404. Upload, list,
metadata, download, and denied access events are recorded using a serialized
hash chain. The chain is not verified or protected from a privileged database
operator, so it is not tamper-proof. Named-user grants and revocation, complete
PDF validation/malware scanning, refresh tokens, email verification, password
recovery, user suspension management, production storage, and a restricted
production DB role remain follow-up work. An endpoint is not secure merely
because its UI is hidden.

## First commit plan

`feat(sprint-0): establish TrustVault project foundation` — solution/workspace,
initial data model and migration, auth boundary smoke tests, local development
setup, CI, and architecture/security notes. Keep later user stories in separate
feature branches and commits.
