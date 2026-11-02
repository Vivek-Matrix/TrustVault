# Prototype hosting plan

## Selected services

- **Render Web Service (Docker):** ASP.NET Core API serves the Angular static
  build from the same origin for the initial prototype.
- **Render PostgreSQL:** relational database.
- **Cloudflare R2:** private object storage for uploaded document binaries.
- **GitHub Actions:** CI; Render can auto-deploy from the connected branch.

The root [Dockerfile](../Dockerfile) builds the Angular frontend and .NET API
into one image. Render's assigned `PORT` is honored by the API. Configure the
service health check to `GET /api/system/health`; use
`GET /api/system/ready` to confirm that the database is reachable.
The app accepts Render's PostgreSQL `postgres://`/`postgresql://` URL through
`DATABASE_URL` and converts it to an encrypted Npgsql connection. It also
accepts a standard Npgsql connection string in `ConnectionStrings__TrustVault`.

This plan is sized for a small prototype, but document confidentiality still
requires production-grade authorization. Hosting providers do not enforce the
application's per-document access rules.

## Manual account preparation

1. Create/sign in to GitHub and confirm the repository is private or public
   according to the review requirement. Never commit credentials or environment
   files.
2. Create/sign in to Render. Enable account MFA and review current service,
   database, bandwidth, and storage pricing.
3. Set a monthly spending target and provider budget/usage alerts where available.
   Alerts are not guaranteed spending caps. Avoid provisioning paid services
   until implementation and expected costs are reviewed.
4. Create/sign in to Cloudflare and enable account MFA.
5. Do not create an R2 bucket or API token until the backend storage integration
   is implemented. At that point, create a dedicated private bucket and a
   narrowly scoped token for that bucket only. Never share the token in chat or
   add it to Git.
6. Do not manually create production database tables or paste cloud credentials
   into the repository. The image includes a one-shot migrator at
   `dotnet migrator/TrustVault.Migrator.dll`; run it as a Render pre-deploy
   command before the API release starts. This command requires a service plan
   that supports pre-deploy commands.

No provider credentials are needed to continue coding locally or in GitHub CI.
When deployment is ready, sign in directly to provider dashboards and add the
required secrets there.

## Render setup after the code is ready

1. In Render, create a managed PostgreSQL database and a Docker Web Service
   connected to the TrustVault GitHub repository and the branch you intend to
   deploy.
2. Set the Web Service's Dockerfile path to `Dockerfile` at the repository root.
   Do not set a separate frontend build or start command; the image builds the
   Angular app and starts the API.
3. Configure the database's **internal** URL as the Web Service's `DATABASE_URL`
   environment variable. Keep the database and Web Service in the same Render
   region. Do not use the external database URL for the service-to-database
   connection.
4. Add these environment variables to the Web Service. Enter secret values only
   in Render's dashboard; do not paste them into chat or commit them:

   - `ASPNETCORE_ENVIRONMENT` = `Production`
   - `DATABASE_URL` = the PostgreSQL internal URL
   - `Jwt__SigningKey` = a cryptographically random value with at least 32 UTF-8
     bytes
   - `Jwt__Issuer` = `TrustVault`
   - `Jwt__Audience` = `TrustVault`

5. Set the health check path to `/api/system/health`. Add the pre-deploy command
   `dotnet migrator/TrustVault.Migrator.dll` so EF migrations run before each
   release. Confirm the selected Web Service plan supports pre-deploy commands
   and check current pricing before creating paid resources.
6. Deploy, then verify the service health check is green and open
   `/api/system/ready`. A `200` response confirms database connectivity; a
   `503` means to inspect Render's service logs and database URL configuration.

The initial deployment serves frontend and API on one origin, so
`Frontend__Origin` and R2 credentials are not needed. Do not provision R2 until
the application implements private document storage. Never publish secret
values, signing keys, database URLs, or R2 credentials in GitHub Actions logs,
build arguments, or the Angular bundle.

## Deployment gate

Do not expose document upload/download publicly until authorization and
revocation integration tests pass against server endpoints and a private storage
bucket. Before a judge-facing deployment, also require HTTPS, migrations through
a controlled release step, backups/restore verification, bounded upload limits,
rate limits, security headers, audit-chain verification, and documented
incident/secret-rotation procedures. These are not implemented in Sprint 0.
