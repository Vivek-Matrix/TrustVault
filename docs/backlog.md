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
| Solution, API, domain, infrastructure, migrator, tests, and Angular workspace exist | Done | The ASP.NET solution and Angular workspace are present. Authentication UI and the first owner-only document API are implemented; document UI and sharing are not. |
| Local PostgreSQL and initial schema/migration are defined | Done | Compose and the initial EF migration exist. The migrator applied migrations successfully to both the existing local database and a separate empty PostgreSQL database; the existing named volume was preserved. |
| Authentication skeleton and deny-by-default boundary work | Partial | Registration/login, Viewer-only self-registration, short-lived JWTs, and protected-route tests exist. Refresh tokens, email verification, account-management UI, and revocation of already-issued tokens remain open. |
| README and local run instructions exist | Done | Root README documents setup, verification, and known limitations. |
| CI pipeline is green | Done | The owner reports all GitHub Actions CI checks passed for the current Sprint 1 changes. |
| Seed script and sample users/documents exist | Not done | No seed script or demo accounts/documents are included. |
| Secure deployment target exists | Not done | AWS is the selected provider and `ap-southeast-2` is the requested Region, but no AWS application resources or public deployment exist. The reported $100 credits are not a monthly spending cap. |
| AWS Agent Toolkit is usable with the project's AWS profile | Partial | AWS CLI v2 and `uv` are installed, `trustvault-dev` sign-in succeeded, and the skill catalog returned 119 skills. The setup wizard was not run because the workspace already has a remote HTTP AWS MCP endpoint that must not be overwritten. See [deployment prerequisites](./deployment.md#aws-agent-toolkit-setup). |

Do not mark Sprint 0 done until the remaining required checks are completed or
the product owner explicitly accepts a documented scope change. Do not deploy
confidential documents: document authorization, immediate revocation, secure
storage, and complete audit behavior are not implemented.

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

### Sprint 2 in progress: secure owner-only document lifecycle

**Goal:** Deliver the first real document API slice with server-enforced
ownership, private file storage, and test evidence that a different user cannot
read a document. Keep the existing one-day sprint timebox; carry unfinished
work forward rather than relaxing a security acceptance criterion.

This is the priority over refresh-token renewal because the product brief's
highest-weight requirements are document isolation, access evidence, and
working document operations. Refresh-token renewal is useful but is not a
stated product requirement, and the current 15-minute access token is already
short-lived.

**Active slice of TV-004 (8-point target): secure owner-only upload and retrieval**

Implementation now includes authenticated upload/list/metadata/download
endpoints, a local private-filesystem provider, PDF extension/signature/size
checks, owner-based authorization, and serialized hash-chained audit writes.
Integration tests cover owner success, anonymous denial, cross-user denial,
invalid uploads, and size limits. The Angular screen supports owner upload,
listing, refresh, and download. All 28 backend tests and 11 frontend tests pass.
No AWS resources or real documents are involved.

- Add authenticated upload, owner-scoped listing, and download endpoints. Derive
  the owner from the authenticated identity; never trust a client-supplied
  owner, filesystem path, or storage key.
- Validate file size and allowed type/content before storing. Generate opaque
  storage keys, keep content private, and never return a public or reusable
  storage URL.
- Check ownership on every metadata and download request. Return no document
  metadata or bytes to a different user, including when they guess a valid ID.
- Write audit events for successful upload/download and denied cross-user
  access. Hash-chain writing is implemented, but verification, external
  anchoring, and restricted production database roles are still required before
  describing the audit as tamper-proof.
- Add API integration tests for owner success, anonymous denial, cross-user
  metadata/download denial, invalid or oversized uploads, and audit outcomes.
  Update API, threat, architecture, and local setup documentation.
- Add an authenticated Angular document screen for upload, owner-only listing,
  refresh, and download with clear loading/error states.
- Verified: full solution test run passes all 28 backend tests; .NET formatting
  verification passes; EF reports no pending model changes. Frontend lint,
  all 11 unit tests, and production build pass.

**Explicitly out of scope:** named-user grants and revocation, document editing
and deletion, refresh-token rotation, malware scanning, AWS provisioning, and
production deployment. Keep storage behind a private
provider boundary; choose and validate the production AWS storage service only
after deployment prerequisites and cost are confirmed. The current local
development storage must not be presented as production-ready.

**Definition of done:** acceptance tests prove that a second user cannot obtain
the first user's document metadata or bytes; failed validation leaves no
retrievable document; permitted and denied attempts are represented in audit
records; no secrets or real confidential files are used in tests; and docs
accurately state remaining audit-integrity and deployment gaps.

**Next slices:** TV-006/TV-007 for named-user read/edit grants and immediate
revocation on every request; TV-009 for a tamper-evident audit chain and
restricted runtime database role; TV-003a for refresh-token lifecycle. Revisit
ordering if the one-day estimate proves too large, but keep the owner-only API
and cross-user denial tests as the first security gate.

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
