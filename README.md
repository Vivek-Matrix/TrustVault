# TrustVault

TrustVault is a local document-sharing prototype. It supports authentication,
private PDF/TXT/DOC/DOCX upload/download, named-user READ/EDIT sharing, document
editing, version history, and audit-chain verification. It is not
production-ready. Refresh-token issuance, rotation, and revocation are
implemented; production deployment is not.

## Architecture

- `frontend/`: Angular 21 and TypeScript.
- `backend/TrustVault.Api`: ASP.NET Core 10 API, admin-managed accounts/login,
  short-lived JWT validation, deny-by-default authorization policy, and
  owner and active-grant document authorization.
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
v2, `uv`, and the Agent Toolkit are installed; the `default` profile is
authenticated for `ap-southeast-2`. The existing workspace AWS MCP entry uses
a remote HTTP endpoint and was preserved. TrustVault is not deployed to AWS
and no AWS application or database resources have been created. The AWS
project is on the Free plan with $100 in credits; credits are not a monthly
spend limit. The owner requested a full cost estimate before selecting a
recurring budget or approving resources. The preferred document design is
private S3 quarantine with asynchronous scanning, but it is not implemented.
The current Docker image is built by CI; it is not published to AWS.

The earlier Render deployment instructions are superseded. See the
[AWS deployment status and prerequisites](docs/deployment.md) before taking
deployment steps. Do not upload real confidential files: although server-side
sharing and revocation are implemented locally, production-grade storage and
audit integrity controls are not.

## Administrator oversight

The Administrator can view platform counts, investigate and export filtered
audit metadata, verify the stored audit chain, review rule-based security
alerts, search document metadata and permission history, revoke a named-user
grant with a reason, reassign a document to an active member, reset member
credentials, and inspect read-only policy settings. These controls do not
provide document content access; the Administrator still needs a document
grant to read or download content. Ownership reassignment revokes all current
grants. Named-user shares do not support guest links or expiry.

Alert rules currently flag five audited denials from one actor/IP in ten
minutes or twenty successful downloads from one actor/IP in an hour. They do
not send notifications and do not capture anonymous authorization failures.
Settings are view-only; infrastructure, scanner, password, session, and upload
policy changes require reviewed deployment configuration. MFA,
self-service password recovery, retention/recovery, deletion approval,
quarantine, and independent audit anchoring remain unfinished.

See [docs/architecture.md](docs/architecture.md), [docs/api.md](docs/api.md),
[docs/backlog.md](docs/backlog.md), and [THREATS.md](THREATS.md).

## Requirements

- .NET 10 SDK
- Node.js 20.19+ or 22.12+ and npm
- Docker Desktop with Docker Compose

## Run locally

From the repository root in PowerShell:

```powershell
docker compose up -d --build --force-recreate postgres clamav converter converter-dev-proxy
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql
# The role setup script uses local-only passwords; never reuse them outside Compose.
$env:ConnectionStrings__TrustVault = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_runtime;Password=runtime_local"
$keyBytes = New-Object byte[] 32
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($keyBytes)
$rng.Dispose()
$env:Jwt__SigningKey = [Convert]::ToBase64String($keyBytes)
$env:Jwt__Issuer = "TrustVault"
$env:Jwt__Audience = "TrustVault"
$env:AdminBootstrap__Email = "admin@gmail.com"
$secureAdminPassword = Read-Host "Set the bootstrap administrator password" -AsSecureString
$env:AdminBootstrap__Password = [System.Net.NetworkCredential]::new("", $secureAdminPassword).Password
$env:Frontend__Origin = "http://localhost:4200"
$env:DocumentConversion__Endpoint = "http://localhost:6100/"
$env:ConnectionStrings__TrustVaultMigrator = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_migrator;Password=migrator_local"
# Optional: documents are stored outside wwwroot at this path by default.
# $env:Storage__RootPath = "C:\TrustVaultData\documents"
dotnet tool restore
dotnet run --project backend\TrustVault.Migrator
docker compose exec -T postgres psql -U trustvault_app -d trustvault -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/10-setup-roles.sql
Remove-Item Env:ConnectionStrings__TrustVaultMigrator
dotnet run --project backend\TrustVault.Api
```

In another PowerShell window:

```powershell
Set-Location frontend
npm ci
npm start
```

The API listens at `http://localhost:5106` and the Angular app at
`http://localhost:4200`. Health, login, and refresh are anonymous; public
registration is disabled. On the first API startup after migration, the API
creates the sole Administrator from `AdminBootstrap__Email` and
`AdminBootstrap__Password`.
Store the password in a local secret manager or environment, not in source
control. Once the administrator exists, the bootstrap settings can be removed.
The administrator can create member accounts by email; every new account
automatically receives the Viewer base role, with no role selector. Document
ownership is assigned to the uploader for each document, and owners grant
document-specific access. Each new member receives a one-time temporary
password that must be changed at first sign-in. Removing a member disables
their account and revokes their tokens and active grants immediately, while
retaining their documents and audit records. Adding a previously removed email
reactivates the same identity with a new temporary password; its documents are
retained, while old access grants remain revoked.
Authentication returns a 15-minute bearer token by default,
configurable to 5-30 minutes with `Jwt:AccessTokenLifetimeMinutes`. The Angular
client keeps the token in memory only, so closing or reloading the page signs
the user out. The development API documentation is at `/swagger`.

After signing in, the Angular screen lets you upload, list, refresh, and download
PDF, TXT, DOC, and DOCX files. TXT documents can be edited directly. Word
documents use the isolated LibreOffice converter service; Word editing keeps
text but does not preserve formatting, images, tables, or other embedded
content. Saving a legacy `.doc` produces `.docx`; the original is kept in
previous versions. The in-app page editor and PDF replacement are available
for PDFs. Owners can grant READ or EDIT access to another
existing account and revoke an active grant. READ permits viewing and download;
EDIT also permits changing title/details, editing TXT/Word text, editing PDF
pages, and replacing a PDF.
Uploads are limited to 10 MiB. The server validates the extension and
format-specific signature or structure, then stores files under an opaque
generated key outside the static web root and scans them with the local ClamAV
service. Download and edit are enabled only when the scan status is `CLEAN`;
failed scans can be retried by the owner. TXT files must use UTF-8 or Unicode
with a byte-order mark. The current filesystem provider and local scanner
configuration are for development only; they are not a production storage or
scanning design. Use dummy files, not confidential documents. Wait for a
`CLEAN` status before downloading or editing.

For local development, the API reaches the converter through a resource-limited
Nginx relay bound to `127.0.0.1:6100`. The converter itself has no published
host port and remains on an internal Docker network; the relay is not intended
for production.

### Try the document screen

1. Keep the API running in the first PowerShell window.
2. In a second PowerShell window, run `Set-Location frontend` and then
   `npm start`.
3. Open `http://localhost:4200` and sign in as the bootstrap administrator.
4. In **Manage members**, add a member by email. The account receives the
   Viewer base role automatically. Deliver the displayed temporary password
   securely out-of-band. Sign out, then sign in as that member and change the
   temporary password.
5. In **Documents**, enter a title/category, choose a small dummy PDF, TXT,
   DOC, or DOCX file, and select **Upload file**. Wait for the scan status to
   show `clean`; if it shows `scan failed`, confirm the local ClamAV service is
   ready and select **Retry scan**. Only clean files can be downloaded or
   edited. Choose **Edit** to edit TXT or Word
   text, or to open the page editor for a PDF. The converter service must be
   running to edit Word files.
5. Sign out, create a second test account, and confirm its document list is
   empty. Automated API tests separately verify that a different account
   cannot retrieve another owner's document, even with its ID.
6. Sign back in as the owner. On the document, choose **Manage access**, enter
   the second account's email, leave permission as **Read**, then choose
   **Grant access**.
7. Sign out and sign in as the second account. Choose **Refresh list**; the
   shared document should appear with its permission, and **Download** should
   work.
8. Sign back in as the owner, choose **Manage access**, then **Revoke** beside
   the second account and confirm. Sign back in as that account and refresh:
   the document should no longer be listed. The API integration tests also
   verify that revoked access to its old document ID returns 404.
9. Repeat the sharing steps, this time choosing **Edit**. Sign in as the second
   account and choose **Edit** on the shared document. Try saving a new title,
   edit and save a TXT or Word text file, then use a disposable PDF to add text,
   draw, replace an area, reorder/remove a page, and select **Save edited PDF**.
   Replace the PDF with a different dummy PDF and verify the previous file is
   available under **Previous versions**.
10. Use a disposable PDF with non-sensitive sample text to test **Redact area**.
    Save the result, download it, and verify the covered area is black and the
    old file no longer appears in **Previous versions**. Do not treat this as
    secure deletion from backups or already-downloaded copies.
11. Choose the clock icon on a document to view its creation, read, and edit
    history, including who performed the action and when. Owners and users with
    an active grant can view the history; revoked users cannot.
12. As the owner, choose the trash icon to remove a document. Confirm the
    warning to delete it and revoke every user's shared access. The document
    and its previous versions become unavailable to everyone.

Do not use real confidential files. The browser keeps the access token only in
memory; refreshing or closing the page signs the user out. Use separate
browser profiles or sign out between accounts during this test.

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
an authenticated-user fallback policy. Public registration is disabled. The
sole bootstrap administrator creates Viewer-default members with mandatory
temporary-password changes; client-supplied role values are ignored. Document
ownership is assigned per document to its uploader. Passwords use Identity's configured
requirements and hashing. Failed sign-ins trigger Identity lockout, and
authentication routes have an in-memory per-IP rate limit. Every authenticated
request checks current account suspension and security-stamp state in the
database to invalidate tokens immediately on member removal. Document metadata and downloads are available to
the owner or a user with an active named grant; the server checks grant state
on each request, so revocation takes effect immediately. Unauthorized users
receive 404. Upload, list, metadata, download, grant, revoke, and denied access
events are recorded using a serialized hash chain. The migrator can verify the
hashes and links among stored events with
`dotnet run --project backend\TrustVault.Migrator -- --verify-audit`. This
detects changed records and broken links, but cannot detect tail deletion or a
complete rewrite without external anchoring. The chain is not tamper-proof.
EDIT users can update details and use the in-app PDF editor to add text/draw,
replace text areas, redact, reorder/remove pages, or replace the PDF. Page edits
are flattened to images, so text search, links, forms, signatures, and
accessibility features are lost. Redaction clears app-accessible previous
versions but cannot erase external backups or copies already downloaded.
Production malware-scanning and durable-storage choices, email verification,
password recovery, independent audit anchoring, and production DB role
provisioning remain follow-up work. Locally, uploads
are synchronously scanned by ClamAV; pending, rejected, and failed files are
blocked from download and editing. Existing documents are migrated to a
pending scan state and must be rescanned before use. Local and CI use separate migrator
and restricted runtime database roles; production credentials still need
separate provisioning. Refresh tokens are issued, rotated, and revoked
server-side, while the browser keeps the access token in memory only. An endpoint
is not secure merely because its UI is hidden.

## First commit plan

`feat(sprint-0): establish TrustVault project foundation` — solution/workspace,
initial data model and migration, auth boundary smoke tests, local development
setup, CI, and architecture/security notes. Keep later user stories in separate
feature branches and commits.
