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
| Solution, API, domain, infrastructure, migrator, tests, and Angular workspace exist | Done | The ASP.NET solution and Angular workspace are present. The Angular app is still a shell; the planned feature folders and user-facing flows are not implemented. |
| Local PostgreSQL and initial schema/migration are defined | Done | Compose and the initial EF migration exist. The migrator applied migrations successfully to both the existing local database and a separate empty PostgreSQL database; the existing named volume was preserved. |
| Authentication skeleton and deny-by-default boundary work | Partial | Registration/login, Viewer-only self-registration, short-lived JWTs, and protected-route tests exist. Refresh tokens, email verification, account-management UI, audit events, and revocation of already-issued tokens remain open. |
| README and local run instructions exist | Done | Root README documents setup, verification, and known limitations. |
| CI pipeline is green | Partial | The latest pushed CI run for `5db247e` succeeded. Current local edits are uncommitted and have not been checked by GitHub Actions. |
| Seed script and sample users/documents exist | Not done | No seed script or demo accounts/documents are included. |
| Secure deployment target exists | Not done | AWS is the selected provider and `ap-southeast-2` is the requested Region, but no AWS application resources or public deployment exist. The reported $100 credits are not a monthly spending cap. |
| AWS Agent Toolkit is usable with the project's AWS profile | Partial | AWS CLI v2 and `uv` are installed, `trustvault-dev` sign-in succeeded, and the skill catalog returned 119 skills. The setup wizard was not run because the workspace already has a remote HTTP AWS MCP endpoint that must not be overwritten. See [deployment prerequisites](./deployment.md#aws-agent-toolkit-setup). |

Do not mark Sprint 0 done until the remaining required checks are completed or
the product owner explicitly accepts a documented scope change. Do not deploy
confidential documents: document authorization, immediate revocation, secure
storage, and complete audit behavior are not implemented.

## Sprint 1 progress

Started the first-day authentication slice (TV-002): account registration,
password login, short-lived JWTs, default Viewer role, failed-login lockout,
IP-based request limiting, and a minimal Angular sign-in/register interface.
This does not complete the entire original Sprint 1 scope. Refresh-token
rotation, suspension administration and revocation of already-issued tokens,
email verification, and security-event audit records remain follow-up work.
The owner manually verified registration, sign-out, and sign-in in the local
browser. All workflow checks passed locally: backend format/build/migration/
tests, frontend lint/tests/build/audit, and Docker image build. GitHub Actions
has not run for these uncommitted changes.

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
