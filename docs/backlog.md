# Initial backlog

Story points use a relative Fibonacci scale. Security denial cases are acceptance
criteria, not optional follow-up.

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
