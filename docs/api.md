# API surface

All routes are protected by the API fallback authorization policy unless marked
anonymous. The browser's route guards are not an authorization boundary.

| Method | Route | Access | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | Anonymous; 10 requests/minute/IP | Create a Viewer account and return a short-lived access token |
| POST | `/api/auth/login` | Anonymous; 10 requests/minute/IP | Validate credentials and return a short-lived access token |
| POST | `/api/documents` | Authenticated owner | Upload a PDF; ownership is taken from the signed-in identity |
| GET | `/api/documents` | Owner or active grantee | List non-deleted documents owned by or shared with the caller |
| GET | `/api/documents/{id}` | Owner or active grantee | Read metadata; other users receive 404 |
| GET | `/api/documents/{id}/content` | Owner or active grantee | Download the private PDF; other users receive 404 |
| GET | `/api/documents/{id}/grants` | Document owner | List active named-user grants |
| POST | `/api/documents/{id}/grants` | Document owner | Grant `READ` or `EDIT` permission to an existing account by email |
| DELETE | `/api/documents/{id}/grants/{userId}` | Document owner | Revoke that user's active grant; retain grant history |
| GET | `/api/system/health` | Anonymous | Process health probe |
| GET | `/api/system/ready` | Any authenticated JWT | Readiness probe; returns 503 when PostgreSQL cannot be reached |
| GET | `/api/system/whoami` | Any authenticated JWT | Return the caller's subject and role claims |
| GET | `/swagger` | Anonymous, Development only | Development API documentation |

Registration creates accounts with the `Viewer` role; the request cannot select
or elevate roles. Passwords are validated and hashed by ASP.NET Core Identity.
Login returns the same `401` response for an unknown email, invalid password,
locked-out account, or suspended account. Repeated failed passwords trigger
Identity lockout. Access tokens default to 15 minutes; the configured lifetime
must remain between 5 and 30 minutes. Tokens are held only in Angular memory and
are lost when the page is reloaded or closed.

Document uploads accept PDFs up to 10 MiB. The server checks the `.pdf`
extension and `%PDF-` signature, generates an opaque storage key, and stores the
file in the configured private filesystem directory (default:
`backend/TrustVault.Api/App_Data/documents`). The client cannot set the owner or
storage key. The current filesystem provider is for local development only; it
is not durable production storage, and this implementation does not parse PDFs
or scan for malware.

Upload, list, metadata-read, download, grant, revoke, and denied access
attempts are written to the append-only audit table with a serialized SHA-256
hash chain.
Database transactions serialize audit appends. This is not a claim of
tamper-proof evidence: chain verification, protected external anchoring, and a
production runtime database role unable to alter the schema are still missing.
Owners can share a document with another active registered account by email.
Only the owner can list, create, or revoke grants. A grant is checked on every
list, metadata, and download request; revocation therefore takes effect on the
next request. Revoked grants remain in the database, and changing a permission
revokes the prior grant and creates a new history row. Unknown and unauthorized
document IDs return the same 404, while authenticated denied attempts are
recorded. `EDIT` is accepted and displayed but currently grants only the same
read/download capabilities as `READ`; document editing is not implemented.

Refresh-token rotation, email verification, password reset, administrator user
management, and immediate revocation of already-issued access tokens are not
implemented. `whoami` reads identity claims only and does not imply authorization
to any document. The in-memory IP limiter requires trusted forwarded-header
configuration when deployed behind a proxy; do not treat it as a substitute for
account-level monitoring or an edge rate limit.
