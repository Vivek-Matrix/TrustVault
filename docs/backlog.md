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
| Authentication skeleton and deny-by-default boundary work | Partial | Registration/login, Viewer-only self-registration, short-lived JWTs, and protected-route tests exist. Refresh tokens, email verification, account-management UI, and revocation of already-issued tokens remain open. |
| README and local run instructions exist | Done | Root README documents setup, verification, and known limitations. |
| CI pipeline is green | Done | The owner reports all GitHub Actions CI checks passed for the current Sprint 1 changes. |
| Seed script and sample users/documents exist | Not done | No seed script or demo accounts/documents are included. |
| Secure deployment target exists | Not done | AWS is the selected provider and `ap-southeast-2` is the requested Region, but no AWS application resources or public deployment exist. The reported $100 credits are not a monthly spending cap. |
| AWS Agent Toolkit is usable with the project's AWS profile | Partial | AWS CLI v2 and `uv` are installed, `trustvault-dev` sign-in succeeded, and the skill catalog returned 119 skills. The setup wizard was not run because the workspace already has a remote HTTP AWS MCP endpoint that must not be overwritten. See [deployment prerequisites](./deployment.md#aws-agent-toolkit-setup). |

Do not mark Sprint 0 done until the remaining required checks are completed or
the product owner explicitly accepts a documented scope change. Do not use
confidential documents: local storage and complete audit integrity are not
production-ready.

## Sprint 1 progress

The TV-002 authentication slice is complete: account registration, password
login, short-lived JWTs, default Viewer role, failed-login lockout, IP-based
request limiting, and a minimal Angular sign-in/register interface. The owner
manually verified registration, sign-out, and sign-in in the local browser.
All local checks passed, and the owner reports that all GitHub Actions CI
checks passed. This does not complete every originally planned Sprint 1 item:
refresh-token rotation, suspension administration and revocation of
already-issued access tokens, email verification, and security-event audit
records remain open.

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

**Still out of scope:** document deletion, refresh-token rotation, malware
scanning, AWS provisioning, and production deployment. Keep storage behind a private
provider boundary; choose and validate the production AWS storage service only
after deployment prerequisites and cost are confirmed. The current local
development storage must not be presented as production-ready.

**Definition of done:** acceptance tests prove that a second user cannot obtain
the first user's document metadata or bytes; failed validation leaves no
retrievable document; permitted and denied attempts are represented in audit
records; no secrets or real confidential files are used in tests; and docs
accurately state remaining audit-integrity and deployment gaps.

**Next slices:** external audit protection and a restricted runtime database
role from TV-009; TV-003a for refresh-token lifecycle. Revisit ordering if
estimates change while retaining per-request authorization and cross-user
denial tests as security gates.

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
