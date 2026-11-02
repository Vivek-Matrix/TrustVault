# Sprint 0 architecture

## Trust boundaries

The Angular 21 application is an untrusted client. ASP.NET Core 10 validates bearer
tokens and is the only future boundary for document authorization. PostgreSQL holds identities, document metadata, per-user grants, versions, and
audit events. The selected prototype hosting direction is a Render Docker Web
Service serving both API and Angular on one origin, plus managed PostgreSQL.
Cloudflare R2 is planned for binary files but is not connected yet; the R2 bucket
must remain private and use server-generated object keys before upload/download
stories ship.

## Authorization model

Roles are `Administrator`, `DocumentOwner`, and `Viewer`. Roles describe broad
capabilities, not document access. The planned document ACL is a per-document
grant to a named user with `Read` or `Edit`; only the owner may grant, revoke, or
delete. An administrator does not inherit document access. A revoked grant is
retained for history and takes effect when the server evaluates the active grant
on each request.

The API's fallback policy requires authentication; `/api/system/health`,
`/api/system/ready`, and the
development OpenAPI document are explicit anonymous exceptions. JWT validation
requires a secret key supplied through configuration and rejects keys under 32
UTF-8 bytes. JWT issuance, account suspension enforcement, document ACL handlers,
and refresh-token rotation are not part of Sprint 0.

## Data model

Identity tables are created by ASP.NET Core Identity. `documents` stores metadata
and an opaque storage key, never a client-provided path. `document_grants`
preserves grant/revocation history and permits at most one active grant for a
document/user pair. `document_versions` has a unique version number per
document. `audit_events` stores request/actor/result fields and hash-chain slots. EF saves
reject modifications/deletions and the initial migration adds a PostgreSQL
update/delete trigger; hash creation/verification and restricted production DB
roles are not implemented yet.

The EF model adds indexes for owner/date, category/date, grant user/document,
version ordering, and audit actor/document/time queries. User identifiers,
ownership, permission level, and storage keys must be derived or authorized by
the server in later API stories.

## Planned file and audit controls

- Keep the R2 bucket private and route content through an authorized API stream;
  do not return reusable public URLs.
- Recheck grants on every metadata, download, edit, and permission request.
- Revoke grants in PostgreSQL and make all subsequent access checks read current
  state; avoid long-lived SAS tokens/caches.
- Append audit rows in the same transaction as the corresponding state change.
  Keep the database trigger, use a runtime DB role that cannot alter schema, and
  add a serialized, verified hash chain before claiming tamper resistance.
- Audit both successful and denied security actions without storing file content.

## Configuration

Secrets are supplied via .NET environment variables or a secret manager, never
the Angular bundle. `Jwt:SigningKey` and `ConnectionStrings:TrustVault` are
required by the API. `Frontend:Origin` configures the local CORS origin. Render
and Cloudflare accounts/resources, production deployment, HTTPS, managed
PostgreSQL, R2, runtime DB-role separation, and operational monitoring remain
unconfigured. Render services must receive secrets through their environment
settings; Cloudflare API tokens belong only in the secret store of the future
storage integration and must be narrowly scoped.
