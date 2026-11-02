# Prototype hosting plan

## Selected services

- **Render Web Service (Docker):** ASP.NET Core API.
- **Render Static Site:** Angular production build.
- **Render PostgreSQL:** relational database.
- **Cloudflare R2:** private object storage for uploaded document binaries.
- **GitHub Actions:** CI; deployment automation is a later task.

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
   into the repository. Provision resources and apply migrations only after the
   deployment workflow and database permissions have been reviewed.

No provider credentials are needed to continue coding locally or in GitHub CI.
When deployment is ready, sign in directly to provider dashboards and add the
required secrets there.

## Planned API configuration

The API will need deployment-specific settings supplied through Render's secret
environment settings, including:

- `ConnectionStrings__TrustVault`
- `Jwt__SigningKey`
- `Jwt__Issuer`
- `Jwt__Audience`
- `Frontend__Origin`
- R2 account endpoint, bucket name, and a bucket-scoped access key/secret

Names and storage authentication should be finalized in the implementation
before configuring production. Do not publish secret values, signing keys,
database URLs, or R2 credentials in GitHub Actions logs, build arguments, or the
Angular bundle.

## Deployment gate

Do not expose document upload/download publicly until authorization and
revocation integration tests pass against server endpoints and a private storage
bucket. Before a judge-facing deployment, also require HTTPS, migrations through
a controlled release step, backups/restore verification, bounded upload limits,
rate limits, security headers, audit-chain verification, and documented
incident/secret-rotation procedures. These are not implemented in Sprint 0.
