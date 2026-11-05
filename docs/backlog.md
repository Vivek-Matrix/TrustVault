# Initial backlog

Story points use a relative Fibonacci scale. Security denial cases are acceptance
criteria, not optional follow-up.

## Agile cadence and Sprint 0 status

Each sprint is one day. Story points estimate relative effort; they are
not a promise that a story fits into one day. Sprint planning should select a
small, demonstrable slice that fits the day and carry unfinished work forward
explicitly rather than weakening security acceptance criteria.

**Sprint 0 is not complete.** The repository foundation is substantially in
place, but the Sprint 0 definition of done in the original project brief still
has open items:

| Exit criterion | Status | Evidence / remaining work |
|---|---|---|
| Solution, API, domain, infrastructure, migrator, tests, and Angular workspace exist | Done | The ASP.NET solution and Angular workspace are present. Authentication, document UI, owner-only document operations, and named-user sharing/revocation are implemented locally. |
| Local PostgreSQL and initial schema/migration are defined | Done | Compose and the initial EF migration exist. The migrator applied migrations successfully to both the existing local database and a separate empty PostgreSQL database; the existing named volume was preserved. |
| Authentication skeleton and deny-by-default boundary work | Partial | Login, short-lived JWTs, rotating/revocable refresh tokens, admin-managed member creation/deactivation, and protected-route tests exist. Email verification and password recovery remain open. |
| README and local run instructions exist | Done | Root README documents setup, verification, and known limitations. |
| CI pipeline is green | Done | The owner confirmed Sprint 4 and Sprint 5 GitHub Actions workflows passed. Sprint 6 CI validation is pending. |
| Seed script and sample users/documents exist | Not done | No seed script or demo accounts/documents are included. |
| Secure deployment target exists | Not done | AWS is the selected provider and `ap-southeast-2` is the requested Region, but no AWS application resources or public deployment exist. The reported $100 credits are not a monthly spending cap. |
| AWS Agent Toolkit is usable with the project's AWS profile | Done | AWS CLI v2 and `uv` are installed, the `default` profile is authenticated for `ap-southeast-2`, default AWS skills are installed, and the generated local MCP server entries allow both `trustvault-dev` and `default`. The existing workspace remote HTTP endpoint is preserved. See [deployment prerequisites](./deployment.md#aws-agent-toolkit-setup). |

Do not mark Sprint 0 done until the remaining required checks are completed or
the product owner explicitly accepts a documented scope change. Do not use
confidential documents: local storage and complete audit integrity are not
production-ready.

## Sprint 1 progress

The TV-002 authentication slice originally delivered account registration,
password login, short-lived JWTs, default Viewer role, failed-login lockout,
IP-based request limiting, and a minimal Angular sign-in/register interface.
Sprint 6 later disabled public registration and replaced the account creation
flow with administrator-managed members.
Refresh-token rotation and revocation were added in a later increment and have
automated coverage. Sprint 6 now disables public registration and provides
administrator-managed member creation/deactivation with immediate token
invalidation. Email verification, password recovery, and authentication
security event auditing remain open.

### Sprint 2 progress: secure document lifecycle and named-user access

**Goal:** Deliver the first real document API slice with server-enforced
ownership, private file storage, and test evidence that a different user cannot
read a document. Keep the existing one-day sprint timebox; carry unfinished
work forward rather than relaxing a security acceptance criterion.

This is the priority over refresh-token renewal because the product brief's
highest-weight requirements are document isolation, access evidence, and
working document operations. Refresh-token renewal is useful but is not a
stated product requirement, and the current 15-minute access token is already
short-lived.

**Implemented local slices of TV-004, TV-006, and TV-007**

Implementation includes authenticated upload/list/metadata/download endpoints,
a local private-filesystem provider, PDF/TXT/DOC/DOCX format and size checks,
owner/active-grant authorization, and serialized hash-chained audit writes.
Named-user READ/EDIT grants can be managed by the owner; active grants are
checked on each list/metadata/download/edit request, and revocation is immediate.
Integration tests cover owner success, anonymous denial, cross-user denial,
invalid uploads, size limits, shared listing/download, grant management, and
revocation. The Angular screen supports upload/list/download and grant
management. All 32 backend tests and 13 frontend tests pass. No AWS resources
or real documents are involved.

- Implemented authenticated upload, owner/shared listing, and download
  endpoints. Ownership comes from the authenticated identity; requests cannot
  supply an owner, filesystem path, or storage key.
- Validated size and supported file formats before storage. Opaque keys keep
  files private, and no public or reusable storage URL is returned.
- Enforced ownership or active-grant checks on each list, metadata, and
  download request, including requests using guessed IDs.
- Recorded successful upload/download and denied cross-user access. Sprint 3
  added local verification of stored hash-chain records; external anchoring and
  restricted production database roles remain necessary before describing
  audit evidence as tamper-proof.
- Added API integration tests for owner success, anonymous denial, cross-user
  metadata/download denial, invalid or oversized uploads, and audit outcomes.
  Updated API, threat, architecture, and local setup documentation.
- Added an authenticated Angular screen for upload, listing, refresh, download,
  and permission management with loading/error states.
- Verified: all 43 backend tests, frontend lint, all 14 frontend tests, and the
  production build pass. Owner-guided manual
  verification remains.

**Still out of scope:** malware scanning, AWS provisioning, and production
deployment. Keep storage behind a private provider boundary; choose and validate
the production AWS storage service only
after deployment prerequisites and cost are confirmed. The current local
development storage must not be presented as production-ready.

**Definition of done:** acceptance tests prove that a second user cannot obtain
the first user's document metadata or bytes; failed validation leaves no
retrievable document; permitted and denied attempts are represented in audit
records; no secrets or real confidential files are used in tests; and docs
accurately state remaining audit-integrity and deployment gaps.

**Next slices:** restricted runtime database privileges from TV-009, followed
by external audit anchoring as a separate task. Refresh-token rotation and
revocation have since been implemented. Retain per-request authorization and
cross-user denial tests as security gates.

### Sprint 3: permission clarity and audit-chain verification

**Goal:** Add a local check for altered or disconnected audit records without
claiming protection from a privileged database operator.

- Edit grants were subsequently implemented with server-side authorization
  for metadata updates and PDF replacement/page editing. READ remains
  read-only.
- `dotnet run --project backend\TrustVault.Migrator -- --verify-audit` checks
  record hashes and previous-hash links in timestamp order without migrating or
  changing the database.
- Automated tests cover valid chains, altered event content, incorrect hashes,
  broken links, and a missing middle event. API tests cover grant changes,
  edit authorization, content replacement, version access, and redaction
  version purging.
- Tail deletion and a complete chain rewrite remain undetectable without an
  independently protected external anchor. Production runtime-role separation,
  external anchoring, and deployment remain future work.

### EDIT feature follow-up

EDIT grants now allow users to update document details, replace the current
PDF, or edit pages in the browser. TXT files support direct text editing;
DOC/DOCX text editing uses a separate restricted LibreOffice converter and
saves a simplified DOCX without preserving formatting or embedded media.
Previous source files remain available as versions. The PDF editor supports text overlays, drawing,
visual text replacement, black redaction, page reordering, and page removal.
Edited pages are rasterized and stored as images inside a PDF. Redaction removes
prior-version API records and attempts to delete those files; it cannot erase
offline copies or guarantee removal from filesystem-level backups. Review every
edited output and use test documents until production storage and redaction
handling have been independently assessed.

| ID | Story | Points |
|---|---|---:|
| TV-001 | Establish solution, local PostgreSQL, schema, CI, and auth boundary | 8 |
| TV-002 | Register/login with Identity and short-lived JWTs | 8 |
| TV-003 | Rotate and revoke refresh tokens; suspend accounts | 8 |
| TV-004 | Upload and list owner documents with validation/private storage | 13 |
| TV-005 | Search only documents visible to the caller | 8 |
| TV-006 | Grant/revoke named-user READ and EDIT access | 13 |
| TV-007 | Authorize and stream downloads; prove immediate revocation | 8 |
| TV-008 | Edit metadata/content and owner-only delete | 8 |
| TV-009 | Append-only hash-chained audit writes and admin queries | 13 |
| TV-010 | Angular authentication, document, permission, and activity views | 13 |
| TV-011 | Threat tests, deployment, operational documentation, demo | 13 |

### Sprint 4: database access hardening

**Status:** Implemented and locally verified; GitHub Actions validation is
confirmed successful by the owner. The changes do not deploy AWS resources or
change the hosting decision.

**Timebox:** One day; estimate 8 points. Carry unfinished work forward rather
than weakening a privilege or security acceptance criterion.

**Goal:** Give the API a dedicated PostgreSQL runtime role with only the
permissions required by normal application workflows. Keep schema ownership
and migrations on a separate migrator role, and make audit events append-only
for the runtime role at the database privilege layer as well as in application
code.

**Scope**

- Provision separate, repeatable local/CI database roles and credentials:
  a controlled migrator role that owns schema objects, and a non-owner API
  runtime role. Keep production credentials outside source control and all
  database credentials out of frontend assets; label local-only values clearly.
- Give the migrator the ownership/DDL permissions required to create and
  upgrade the schema. Configure the API and design-time migration tooling to
  use the appropriate credential explicitly; do not silently fall back to
  migration-owner credentials at API startup.
- Grant the runtime role only the connection, schema usage, and table
  operations needed by Identity, document/grant/version workflows, and audit
  reads/appends. Do not grant schema creation, table ownership, role
  administration, or audit-event update/delete/truncate privileges.
- Preserve the database append-only trigger and ensure the API runtime role
  owns neither the audit table nor its trigger function. Treat role setup and
  grants as part of reproducible local setup and CI.
- Document which credential is used by the API, migrator, and EF design-time
  tooling, plus how local developers configure them without committing secrets.

**Acceptance criteria**

1. A clean local database can be initialized and migrated using only the
   migrator credential; the API can then start using only the runtime
   credential.
2. CI provisions both roles, runs migrations with the migrator credential, and
   runs API integration tests with the restricted runtime credential.
3. Representative authenticated flows still succeed under the runtime role:
   admin bootstrap/member creation/login/refresh/logout, document upload/read/edit/delete,
   grant/revoke, version access, and audit append/read/verification.
4. Database-level tests using the runtime role prove schema/table DDL and
   audit-event UPDATE, DELETE, and TRUNCATE are rejected; the runtime role is
   not a table owner, superuser, role administrator, or member of a role that
   grants those capabilities.
5. Document deletion and its grant/version cleanup still work, while audit
   history remains present and immutable.
6. The migrator still applies migrations to an empty database and upgrades an
   existing database; API startup does not run migrations and fails clearly if
   only the migrator connection setting is configured.
7. Format, build, backend tests, frontend lint/tests/build, and container build
   pass in CI. Documentation reflects the actual credential setup and any
   remaining privilege limitations.

**Out of scope:** AWS resource provisioning, production secret-manager
integration, external audit anchoring, malware scanning, and public deployment.
Do not use real confidential documents. Do not claim the audit chain is
tamper-proof: a sufficiently privileged database owner/operator can still
rewrite the database, and external anchoring remains a separate follow-up.

### Sprint 5: private document pipeline

**Status:** Implemented and CI successful (owner confirmed). No cloud resources
were provisioned.

**Goal:** Keep private files unavailable until a scanner returns a clean
verdict, and ensure failures remain visible and retryable without changing
authorization, version, revocation, deletion, or audit behavior.

- Persist scan status, attempt count, and last scan time for current documents
  and saved versions. Existing files are migrated to pending scan.
- Scan uploads and replacement/edit candidates before activation. A rejected
  or failed candidate cannot replace previously clean content.
- Block downloads and content/metadata editing unless the relevant document or
  version is clean. Owners can retry pending/failed scans; stale scanning claims
  can be retried after ten minutes.
- Run a loopback-only ClamAV daemon in local Compose with resource limits.
  Scanner errors and timeouts fail closed. Use deterministic scanner doubles
  and dummy files in tests.
- Keep filesystem storage and ClamAV as local-development providers only.
  Durable production storage and its scanner execution/isolation design remain
  a separately reviewed decision; no AWS services are selected or provisioned.
- Update setup, API, threat, architecture, and deployment documentation.

**Definition of done:** Integration tests cover clean/rejected/failed uploads,
retry success and failure, pending and failed access denial, version scan state,
candidate replacement safety, authorization, deletion/revocation, and audit
status. Backend tests, frontend lint/tests/build, migrations, and container
build must pass. Do not use real confidential documents.

### Sprint 6: administrator-managed member accounts

**Status:** Implemented locally; CI validation pending. No cloud resources were
provisioned.

**Goal:** Only the sole bootstrap Administrator can add or remove members, and
removal immediately invalidates all previously issued credentials without
destroying document or audit history.

- Disable public self-registration. Bootstrap one Administrator from protected
  `AdminBootstrap:Email` and `AdminBootstrap:Password` configuration; never
  commit or send the bootstrap password in chat.
- The Administrator creates members by email only; every new account receives
  the `Viewer` base role automatically, and submitted role values are ignored.
  Document ownership is assigned to the uploader per document; owners manage
  document-specific `READ` and `EDIT` grants. Return a strong temporary
  password once and require a password change before the new member can access
  any other API.
- Deactivate member accounts rather than deleting their identity row. Revoke
  refresh tokens, invalidate access tokens on the next request, revoke active
  grants involving the removed member and their owned documents, and preserve
  documents and audit records. Re-adding a deactivated email reactivates the
  same identity with a new temporary password; old grants stay revoked.
- Record account creation and deactivation in the existing append-only audit
  chain. Keep document authorization per-owner/grant; the Administrator does
  not gain document access by role.

**Definition of done:** API and UI tests prove registration is unavailable,
non-administrators cannot manage members, administrator role cannot be
assigned, temporary passwords must be changed, removed users' existing access
and refresh tokens fail immediately, and member document/audit history is
preserved. Build, backend tests, frontend lint/tests/build, migration checks,
and CI must pass.

### Remaining-gap roadmap

The order below makes each stage a prerequisite or release gate for the next;
it is a planning sequence, not a promise that every item fits in one sprint.

1. **Sprint 4 — Database access hardening (implemented; CI successful).**
   Separate runtime and migration credentials, verify least privilege in
   local/CI PostgreSQL, and retain migration/upgrade coverage.
2. **Sprint 5 — Private document pipeline (implemented; CI successful).**
   Persist per-document/version scan state; scan with local ClamAV; block access
   unless clean; provide owner retries; preserve old clean content if a new
   candidate scan fails. Choose durable production storage and scanning services
   only during a separately reviewed design step.
3. **Sprint 6 — Administrator-managed member accounts (implemented locally;
   CI pending).** Disable public account creation; bootstrap one administrator;
   allow only that administrator to create Viewer-default members without role
   selection and deactivate them; assign document ownership to uploaders,
   require a temporary-password change, and immediately revoke access when a
   member is removed.
4. **Remaining identity and audit assurance.** Add email verification and
   password recovery. Independently anchor audit-chain checkpoints
   outside the application database's administrative boundary; prove that
   altered, removed, or rewritten history is detected without claiming that a
   database-only hash chain is tamper-proof.
5. **Deployment design and approval gate.** Confirm the project Region in AWS
   Settings, spend limit and billing status, service eligibility, networking,
   backup/restore expectations, and a complete cost estimate. Keep Regional
   resources in `ap-southeast-2`. No provisioning begins before the design and
   expected recurring costs are approved.
6. **Production deployment and operations.** Publish an immutable CI image and
   deploy only after the preceding security gates pass. Isolate the API,
   database, and constrained Word converter; inject runtime/migrator secrets
   separately; configure HTTPS, health checks, alerts, backups, restore
   exercises, cleanup, and incident/runbook procedures. Do not upload
   confidential documents until those checks pass.

## TV-001 acceptance and security tests

- A clean checkout restores/builds the Angular and ASP.NET projects.
- The PostgreSQL migration applies to an empty local database.
- Unauthenticated requests are denied by default; health is the only public API
  route in this increment.
- A valid token with matching issuer/audience/signature is accepted, and invalid
  or expired tokens are rejected.
- No development secret, storage credential, or seeded password is committed.

## First commit

`feat(sprint-0): establish TrustVault project foundation`

Include only the initial solution, schema/migration, authorization smoke tests,
local setup, and CI. Subsequent commits should correspond to a reviewed story
branch such as `feature/TV-002-identity-login`.
