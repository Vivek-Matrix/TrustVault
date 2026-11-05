# Sprint 0 architecture

## Trust boundaries

The Angular 21 application is an untrusted client. ASP.NET Core 10 validates bearer
tokens and is the only future boundary for document authorization. PostgreSQL holds identities, document metadata, per-user grants, versions, and
audit events. The API uses a restricted runtime database role; the migrator
owns schema objects and uses a separate credential. AWS is the selected cloud
provider for a future deployment. Specific AWS hosting and database services have not been selected. The existing
Docker image serves the API and Angular app on one origin, but CI only builds
the image; it does not publish or deploy it. No AWS infrastructure is configured.
Do not upload real confidential files. Server-side authorization and revocation
are implemented locally, but production storage and complete audit-integrity
controls are not.

## Authorization model

`Administrator` is the only elevated account role; member accounts are assigned
the `Viewer` base role at creation. Document ownership is not an account role:
the uploader becomes owner of that document. The per-document ACL grants access
to named users; only the document owner may grant or revoke. `Read` grants allow
viewing and download. `Edit` grants additionally allow metadata updates and PDF
replacement or page edits; each operation checks the active grant on the server.
An administrator does not inherit document access. A revoked grant is retained
for history and takes effect when the server evaluates active grants on each
request.

The API's fallback policy requires authentication; health, login, refresh, and
the development OpenAPI document are explicit anonymous exceptions. Public
registration is disabled. The sole Administrator is bootstrapped from protected
configuration; only that account can create members. Member creation accepts
only an email address and always assigns the base `Viewer` role; client-supplied
role values are ignored. Document ownership is a per-document relationship
assigned to the uploader, not an account role. Owners grant or revoke `READ` or
`EDIT` access to named users for each document. Member creation issues a
one-time temporary password and requires a password change before any other
authenticated operation. The
database readiness endpoint remains protected by the fallback policy. JWT
validation requires a secret key supplied through configuration and rejects
keys under 32 UTF-8 bytes. Every authenticated request also checks the user's
active status and security stamp in PostgreSQL so administrative deactivation
and password changes immediately invalidate existing access tokens. Refresh
tokens are issued, hashed, rotated, and revoked server-side. A unique database index and API guards protect the
sole primary administrator. Account create/deactivate operations are appended
to the existing hash chain; this does not protect against a privileged database
operator rewriting the chain.

Administrator oversight APIs are separately role-protected. They expose
platform aggregates, audit metadata, active/revoked grants, and document
metadata, but do not grant content access. Admin credential resets rotate the
security stamp and invalidate existing tokens. Admin permission revocation
requires a reason and is checked live by the existing document authorization
path. Ownership reassignment requires an active non-administrator member and
revokes every active grant in the same transaction. The first alert rules are
fixed heuristics over recorded events (5 denials per actor/IP in 10 minutes;
20 successful downloads per actor/IP in one hour), not configurable monitoring.
The admin settings view is read-only because security/infrastructure values
must be changed through reviewed deployment configuration.

## Data model

Identity tables are created by ASP.NET Core Identity. `documents` stores metadata
and an opaque storage key, never a client-provided path. Document listing,
metadata, and downloads authorize the owner or a current active grantee on
every request. `document_grants` preserves grant/revocation history and permits
at most one active grant for a document/user pair. `document_versions` has a unique version number per
document. Documents and versions persist a scan status (`PendingScan`,
`Scanning`, `Clean`, `Rejected`, or `ScanFailed`), attempt count, and last scan
time. New data is not accessible unless its status is `Clean`; the migration
marks all existing files pending so they cannot bypass scanning. `audit_events`
stores request/actor/result fields and a serialized
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
  storage. Local development synchronously scans stored files through the
  loopback-only ClamAV daemon; scanner/network errors result in `ScanFailed`.
  Rejected, pending, and failed content remains unavailable through download
  and editing routes. Replacement/edit candidates are scanned before activation,
  preserving the old clean version on rejection or scanner failure. Owners can
  retry pending and failed scans, including stale in-progress claims. The local
  scanner and filesystem provider are not a production storage/scanning design;
  select and review those choices before provisioning. Quotas and production
  private object storage remain future work. The page editor and content-
  replacement operation remain PDF-only; TXT and Word text use a separate text
  editor.
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
- Upload metadata, scan result, and its audit event commit in one database
  transaction. Denied metadata/download attempts and other successful document
  operations are recorded. The local migrator can verify hashes and links for
  stored records. External anchoring and protection from a privileged database
  operator remain necessary before claiming tamper resistance.

## Configuration

Secrets are supplied via .NET environment variables or a secret manager, never
the Angular bundle. `Jwt:SigningKey` and `ConnectionStrings:TrustVault` are
required by the API. The migrator and EF design-time tools use
`ConnectionStrings:TrustVaultMigrator`; the API does not fall back to that
credential. Local Compose and CI provision separate roles, with local-only
credentials documented in the root README. Production role provisioning,
secret injection, AWS resources, HTTPS, managed PostgreSQL, durable private
object storage, and operational monitoring remain unconfigured. AWS
credentials and application secrets must not be committed to source control or
included in the frontend bundle.
