# API surface

All routes are protected by the API fallback authorization policy unless marked
anonymous. The browser's route guards are not an authorization boundary.

| Method | Route | Access | Purpose |
|---|---|---|---|
| POST | `/api/auth/register` | Anonymous; 10 requests/minute/IP | Create a Viewer account and return access and refresh tokens |
| POST | `/api/auth/login` | Anonymous; 10 requests/minute/IP | Validate credentials and return access and refresh tokens |
| POST | `/api/auth/refresh` | Anonymous; 10 requests/minute/IP | Rotate a valid refresh token and issue a new access/refresh token pair |
| POST | `/api/auth/logout` | Authenticated | Revoke the caller's refresh token |
| POST | `/api/documents` | Authenticated owner | Upload a supported file; ownership is taken from the signed-in identity |
| POST | `/api/documents/{id}/scan/retry` | Document owner | Retry a pending or failed scan |
| GET | `/api/documents` | Owner or active grantee | List non-deleted documents owned by or shared with the caller |
| GET | `/api/documents/{id}` | Owner or active grantee | Read metadata; other users receive 404 |
| GET | `/api/documents/{id}/audits` | Owner or active grantee | View document creation, read, and edit activity with actor, result, and UTC timestamp |
| GET | `/api/documents/{id}/content` | Owner or active grantee | Download the private file; other users receive 404 |
| DELETE | `/api/documents/{id}` | Document owner | Remove the document, revoke every active grant, and delete current and previous stored content |
| PUT | `/api/documents/{id}` | Owner or active EDIT grantee | Change title, description, and category |
| GET | `/api/documents/{id}/editable-text` | Owner or active EDIT grantee | Read TXT or extracted Word text for editing |
| PUT | `/api/documents/{id}/editable-text` | Owner or active EDIT grantee | Save TXT text or convert edited Word text to DOCX; preserves the previous version |
| PUT | `/api/documents/{id}/content` | Owner or active EDIT grantee | Replace the PDF; optionally clear previous versions for redaction |
| GET | `/api/documents/{id}/versions` | Owner or active grantee | List saved previous PDF versions |
| GET | `/api/documents/{id}/versions/{version}/content` | Owner or active grantee | Download a saved previous PDF version |
| POST | `/api/documents/{id}/versions/{version}/scan/retry` | Document owner | Retry a pending or failed scan for a saved version |
| GET | `/api/documents/{id}/grants` | Document owner | List active named-user grants |
| POST | `/api/documents/{id}/grants` | Document owner | Grant `READ` or `EDIT` access to an existing account by email |
| DELETE | `/api/documents/{id}/grants/{userId}` | Document owner | Revoke that user's active grant; retain grant history |
| GET | `/api/system/health` | Anonymous | Process health probe |
| GET | `/api/system/ready` | Any authenticated JWT | Readiness probe; returns 503 when PostgreSQL cannot be reached |
| GET | `/api/system/whoami` | Any authenticated JWT | Return the caller's subject and role claims |
| GET | `/swagger` | Anonymous, Development only | Development API documentation |

Registration creates accounts with the `Viewer` role; the request cannot select
or elevate roles. Passwords are validated and hashed by ASP.NET Core Identity.
Login returns the same `401` response for an unknown email, invalid password,
locked-out account, or suspended account. Repeated failed passwords trigger
Identity lockout. Registration and login issue an access token and a refresh
token; refresh tokens are stored as hashes, rotated on use, and revoked on
logout. Access tokens default to 15 minutes; the configured lifetime must
remain between 5 and 30 minutes. The Angular client holds the access token only
in memory and does not persist it across page reloads.

Document uploads accept PDF, UTF-8/Unicode TXT, legacy DOC, and DOCX files up
to 10 MiB. The server validates the extension and a format-specific signature
(including the DOCX package/XML structure), generates an opaque storage key,
and stores the file in the configured private filesystem directory (default:
`backend/TrustVault.Api/App_Data/documents`). MIME types supplied by the client
are not trusted. The client cannot set the owner or storage key. The current
filesystem provider and ClamAV service are local development components only;
they are not durable production storage or an approved production scanning
design. New uploads receive `PENDING_SCAN` until scanning produces `CLEAN`,
`REJECTED`, or `SCAN_FAILED`. Scan failures are logged and remain unavailable;
owners can retry pending or failed scans. A scan in progress is represented by
`SCANNING`; stale scans can be retried after ten minutes. Existing documents and
versions are migrated to `PENDING_SCAN` and must pass a scan before use.

Only content with status `CLEAN` can be downloaded, read for editing, or
changed. This applies to current files and saved versions; pending, rejected,
failed, and in-progress scan states are denied with HTTP 409. A replacement or
edit candidate is scanned before it becomes active; a rejected candidate returns
422, and an unavailable scanner returns 503 without replacing the existing
clean content. Uploads are persisted with their final scan state so a client
does not need to guess whether an error caused a duplicate upload. The
in-app page editor and content-replacement endpoint remain PDF-only.

For local development, Docker Compose starts a ClamAV daemon bound to
`127.0.0.1:3310`; configure `MalwareScanning:ClamAv:Host`,
`MalwareScanning:ClamAv:Port`, and `MalwareScanning:ClamAv:TimeoutSeconds` for
other local arrangements. ClamAV may need time to download signatures after its
first start. A scanner connection failure never marks a file clean.

TXT content is editable in the browser. Editing DOC/DOCX requires the separate
LibreOffice converter service on the private converter network. It receives
only the document bytes, has no database credentials or document-storage
mounts, has no published host port, and is configured with resource limits.
Local development uses a loopback-only Nginx relay on a separate network; the
converter remains on its internal network. In production, connect the API to
the converter over a private network instead of deploying the development
relay. Word files are converted to plain text for editing and saved as a simple
DOCX; formatting, images, tables, and other embedded content are not preserved.
Editing a legacy DOC changes the current file to DOCX while keeping the
original DOC as a previous version. Never expose the converter service publicly
or attach it to the database or document-storage network.

Upload, list, metadata-read, download, metadata-update, text-edit read/save,
content-replacement, version, scan retry, grant, revoke, and denied access attempts are
written to the append-only audit table with a serialized SHA-256 hash chain.
Database transactions serialize audit appends. This is not a claim of
tamper-proof evidence: the local verifier checks the records currently present.
The local/CI API role cannot alter the schema or update, delete, or truncate
audit records; production must provision the same role separation. External
anchoring remains missing.
The Document audits control displays successful and denied creation/read/edit/scan
events with the actor's email and timestamp in the viewer's local time. Only
the owner and users with an active document grant can view that history;
revoking a grant also revokes audit-history access.
Owners can share a document with another active registered account by email.
Only the owner can list, create, or revoke grants. A grant is checked on every
list, metadata, download, edit, and previous-version request; revocation
therefore takes effect on the next request. `READ` permits viewing and download.
`EDIT` additionally permits changing the title, description, category, TXT or
Word text, PDF pages, and current PDF file. Revoked grants remain in the
database, and changing a permission revokes the prior grant and creates a new
history row. Unknown and unauthorized document IDs return the same 404, while
authenticated denied attempts are recorded.
Removing a document is owner-only and requires confirmation in the UI. The
document becomes unavailable immediately, all active grants are marked revoked,
and current and previous stored content is deleted. The removal is retained in
the audit history; if physical storage cleanup fails, the API reports an error
and logs the failed cleanup for follow-up.

The in-app editor can add text and strokes, cover an area and add replacement
text, permanently black out an area, reorder pages, and remove pages. Saving
page edits flattens every retained page into an image-based PDF. This removes
the source text and hidden page content, but also removes text search/selection,
links, forms, signatures, and accessibility features; image quality can change.
Users must review the saved file. Redaction clears previous versions from the
application and attempts to remove their stored files. This is not a guarantee
of secure erasure from filesystem backups or copies already downloaded.
Changing existing text is a visual cover-and-replace operation, not a change to
the original PDF text objects.

Replacing a PDF stores the former file as a previous version. Active READ and
EDIT grantees can download those versions. A redaction save removes all previous
version records so the old content is no longer available through the API.
Uploads and editor output are limited to 10 MiB; the in-app editor supports up
to 100 pages.

The migrator can check the stored audit chain without changing the database:
`dotnet run --project backend\TrustVault.Migrator -- --verify-audit`. It checks
records in timestamp order, verifies each event hash, and checks every link
from the first event through the latest stored event. It exits with code 0 for
a valid chain and code 1 when it finds a mismatch. A successful check only
verifies the records currently present: it cannot detect deletion of the tail
or a complete rewrite of the chain without a separately protected external
anchor. The command does not apply database migrations.

Email verification, password reset, administrator user management, and
immediate revocation of already-issued access tokens are not implemented.
`whoami` reads identity claims only and does not imply authorization to any
document. The in-memory IP limiter requires trusted forwarded-header
configuration when deployed behind a proxy; do not treat it as a substitute for
account-level monitoring or an edge rate limit.
