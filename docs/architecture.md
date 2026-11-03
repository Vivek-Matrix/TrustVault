# Sprint 0 architecture

## Trust boundaries

The Angular 21 application is an untrusted client. ASP.NET Core 10 validates bearer
tokens and is the only future boundary for document authorization. PostgreSQL holds identities, document metadata, per-user grants, versions, and
audit events. AWS is the selected cloud provider for a future deployment.
Specific AWS hosting and database services have not been selected. The existing
Docker image serves the API and Angular app on one origin, but CI only builds
the image; it does not publish or deploy it. No AWS infrastructure is configured.
Do not upload real confidential files. Server-side authorization and revocation
are implemented locally, but production storage and complete audit-integrity
controls are not.

## Authorization model

Roles are `Administrator`, `DocumentOwner`, and `Viewer`. Roles describe broad
capabilities, not document access. The per-document ACL is a grant to a named
user; only the owner may grant or revoke. `Read` grants allow viewing and
download. `Edit` grants additionally allow metadata updates and PDF replacement
or page edits; each operation checks the active grant on the server. An
administrator does not inherit document access. A revoked grant is retained
for history and takes effect when the server evaluates active grants on each
request.

The API's fallback policy requires authentication; health, registration/login,
and the development OpenAPI document are explicit anonymous exceptions. The
database readiness endpoint remains protected by the fallback policy. JWT
validation requires a secret key supplied through configuration and rejects
keys under 32 UTF-8 bytes. Registration assigns the Viewer role, and issued
access tokens default to 15 minutes. Account suspension administration,
revocation of already-issued tokens, and refresh-token rotation remain future
work.

## Data model

Identity tables are created by ASP.NET Core Identity. `documents` stores metadata
and an opaque storage key, never a client-provided path. Document listing,
metadata, and downloads authorize the owner or a current active grantee on
every request. `document_grants` preserves grant/revocation history and permits
at most one active grant for a document/user pair. `document_versions` has a unique version number per
document. `audit_events` stores request/actor/result fields and a serialized
SHA-256 hash chain. Audit writes are serialized with a PostgreSQL transaction
advisory lock. EF saves reject modifications/deletions and the initial migration
adds a PostgreSQL update/delete trigger. The migrator's `--verify-audit` command
checks stored event hashes and links in order. External anchoring and restricted
production database roles are not implemented, so verification cannot detect
tail deletion or a complete rewrite by a privileged database operator; this is
not tamper-proof evidence.

The EF model adds indexes for owner/date, category/date, grant user/document,
version ordering, and audit actor/document/time queries. User identifiers,
ownership, permission level, and storage keys are derived or authorized by the
server.

## File and audit controls

- The current local provider stores uploaded documents under a configured filesystem root
  outside the static web root and streams them only through the authorized API.
  The provider is not suitable for production or multiple application replicas.
- Uploads are limited to 10 MiB and support PDF, TXT, DOC, and DOCX. The API
  validates the extension and format-specific signature/structure before
  storage. Malware scanning, quotas, and production private object storage
  remain future work. The page editor and content-replacement operation remain
  PDF-only; TXT and Word text use a separate text editor.
- TXT editing is performed by the API. DOC/DOCX conversion runs in a separate
  LibreOffice container that has no database credentials or document-storage
  mount, has no published host port, is reachable only over an internal
  converter network, and is constrained by container resource limits. Local
  development uses a resource-limited Nginx relay bound to loopback; the relay
  is not part of the production design. Word text editing saves a simplified
  DOCX and does not preserve formatting or embedded media.
- Every implemented list/metadata/download request checks ownership or an
  active named-user grant. Revocation is checked live, so it takes effect on
  the next request.
- Editing endpoints require ownership or an active `Edit` grant. Replacing a
  PDF preserves its previous contents as a downloadable version. A redaction
  save flattens pages to images, clears version records, and attempts to delete
  prior stored files; filesystem backup copies and downloaded files cannot be
  revoked or reliably erased by the application.
- In the browser editor, existing-text changes are visual cover-and-replace
  operations. Saving page edits produces image-based pages, removing text
  search/selection, links, forms, signatures, and accessibility features.
- Upload metadata and its success audit event commit in one database
  transaction. Denied metadata/download attempts and other successful document
  operations are recorded. The local migrator can verify hashes and links for
  stored records. External anchoring and protection from a privileged database
  operator remain necessary before claiming tamper resistance.

## Configuration

Secrets are supplied via .NET environment variables or a secret manager, never
the Angular bundle. `Jwt:SigningKey` and `ConnectionStrings:TrustVault` are
required by the API. `Frontend:Origin` configures the local CORS origin. AWS
production resources, HTTPS, managed PostgreSQL, durable private object
storage, runtime DB-role separation, and operational monitoring remain
unconfigured. AWS
credentials and application secrets must not be committed to source control or
included in the frontend bundle.
