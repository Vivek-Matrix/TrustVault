# AI prompt log

This log records actual work as it happens. No future prompts or commits are
invented. The first entry records the local hash that was available when it was
written; the repository now has an `origin` remote.

## 2026-11-02 — Sprint 0 foundation

- **Asked:** Use the provided TrustVault product brief and its initial delivery
  request to implement Sprint 0, and stop before Sprint 1.
- **Produced:** Angular 18 and ASP.NET Core 8 project scaffolding, EF Core
  identity/document/grant/version/audit models, JWT validation with a global
  authenticated-user fallback policy, CI and initial docs.
- **What needed correction:** The generated API template targeted .NET 9 and
  exposed a weather-forecast example; neither matched the requested stack or
  TrustVault's secure-by-default shape. The generated frontend was only the
  Angular welcome page, and the scaffolded Angular 18 packages had known
  production vulnerabilities.
- **What I did instead:** Retargeted the backend to .NET 8, removed the sample
  route, required a configured JWT key, added explicit anonymous health access,
  upgraded Angular to patched 21.2.x, and wrote tests for anonymous denial and
  valid/invalid-token handling. Added EF/application guards and a database
  trigger for audit updates/deletes. The audit model is not described as
  tamper-proof because hash-chain writing and restricted DB roles are missing.
- **Commit:** `23bcfcd24106d46159263f7faeaba75cc4f6ad46`
  (`feat(sprint-0): establish TrustVault foundation`; local commit, no remote URL).

## Reused context

- The user-provided TrustVault product brief and Sprint 0 delivery instructions
  are the project-specific context used for this increment.

## 2026-11-02 — Render deployment foundation

- **Asked:** Continue Sprint 0 toward an internet-deployed Render/Cloudflare
  prototype, apply the required code tasks, and give beginner-friendly manual
  instructions.
- **Produced:** Retargeted backend projects and EF tooling to .NET 10; added a
  multi-stage root Dockerfile and a one-shot EF migrator; configured Render port
  binding, database readiness, and Render PostgreSQL URL normalization; fixed
  SPA fallback behavior for unknown API routes; moved the local Compose host
  port to `55432`; documented local password troubleshooting and Render setup.
- **Validation:** .NET format, Release build, and all 11 backend tests passed;
  EF reports no pending model changes. Frontend lint, all 3 tests, production
  build, and production dependency audit passed. Docker image build could not be
  run because Docker CLI is unavailable in this environment. No Render or
  Cloudflare resources were created, and the local database migration remains
  a user-side verification step.
- **Commit:** These changes are currently uncommitted.

## 2026-11-02 — Sprint 0 authorization acceptance

- **Asked:** Provide a final step-by-step Sprint 0 verification and resolve the
  mismatch between the stated requirement that health is the only anonymous API
  route and the anonymous database-readiness endpoint.
- **Produced:** Made `/api/system/ready` require authentication, added tests for
  anonymous denial and authenticated database-status handling, and updated API,
  architecture, README, and deployment documentation.
- **Validation:** .NET format verification, Release build, all 12 backend tests,
  and `git diff --check` passed. These changes still need to be committed,
  pushed, and checked by GitHub Actions.

## 2026-11-02 — AWS hosting direction

- **Asked:** Use AWS instead of Render or Azure for future deployments.
- **Produced:** Updated the README, architecture notes, threat notes, and
  deployment status to identify AWS as the chosen provider and mark the Render
  setup steps as superseded. AWS service selection and deployment are pending;
  no AWS application resources were created. The documented $0 monthly budget
  remains a gate before provisioning.

## 2026-11-02 — Sprint 0 exit review

- **Asked:** Assess whether Sprint 0 is complete, using one-day sprints, and
  make any necessary project changes. The owner reports $100 in AWS credits and
  selected Region `ap-southeast-2`.
- **Produced:** Added a Sprint 0 exit checklist to the backlog. The foundation
  exists and the latest pushed CI run (`5db247e`) passed, but Sprint 0 remains
  open: a fresh-database migration run, seed data, an end-to-end login flow,
  and a secure live deployment target are still missing. Started the existing
  Compose PostgreSQL service and successfully ran the EF migrator against its
  existing named volume without deleting data. Recorded the owner's confirmed
  `ap-southeast-2` Region and $100.00 remaining AWS credits; spend limit still
  needs confirmation. Installed `uv`, configured the existing AWS CLI v2 on
  PATH, authenticated the confirmed `trustvault-dev` profile, and verified the
  Agent Toolkit catalog (119 skills).
- **Toolkit configuration decision:** Preserved the existing workspace remote
  HTTP AWS MCP endpoint. The setup wizard was not run because its
  `AWS_MCP_PROXY_PROFILES` setting applies to a generated local server and
  should not be added to the existing HTTP endpoint.
- **What needed correction:** The backlog had no explicit sprint cadence or
  completion status. Deployment notes described a $0 monthly budget and
  overstated toolkit configuration.
- **What I did instead:** Recorded the evidence and gaps without provisioning
  AWS resources. AWS CLI sign-in and catalog verification succeeded, while the
  local setup wizard remains pending to protect the existing MCP endpoint. The
  migration succeeded against the existing local database and a separate empty
  PostgreSQL database; the temporary database was removed after verification.
  These documentation changes are uncommitted; add a commit reference after
  they are committed.

## 2026-11-02 — Sprint 1 authentication slice

- **Asked:** Start Sprint 1 now; defer manual prerequisites that can be handled
  later.
- **Produced:** Added API registration/login with ASP.NET Core Identity,
  Viewer-only self-registration, 5-30 minute JWT lifetime configuration
  (15-minute default), failed-login lockout, generic invalid-login errors,
  suspended-account login rejection, and in-memory per-IP endpoint rate limits.
  Added a minimal Angular registration/sign-in screen that keeps the access
  token in memory, integration/unit tests, CI PostgreSQL setup, and API/setup
  documentation.
- **What remains:** Refresh-token rotation, email verification, password
  recovery, admin account management, audit event writes, and revocation of
  tokens already issued to suspended accounts remain outside this slice.
- **Validation:** 17 backend tests passed before the final two auth tests were
  added. The expanded backend suite then passed all 20 tests, including role
  escalation, lockout, and suspended-account cases. Frontend lint and all 5
  tests passed, and the final production build completed without style-budget
  warnings. The EF migrator also succeeded against a separate empty database;
  that temporary database was removed without touching the existing volume.
  The Docker image built successfully, and `npm audit --omit=dev` reported no
  vulnerabilities. The owner then manually verified account registration,
  sign-out, and sign-in in the local browser.
- **What needed correction:** The initial build found controller validation
  errors and an overly large component stylesheet. The first test run also
  exposed missing `System.Net.Http.Json` imports.
- **What I did instead:** Corrected the validation-problem response and imports,
  trimmed styles, and added coverage for role-escalation attempts, account
  lockout, and suspended-account sign-in. Also verified migration on an empty
  database. These changes are uncommitted; add a commit reference after they
  are committed.

## 2026-11-02 — Sprint 1 local CI verification

- **Asked:** Guide the owner through testing the authentication flow.
- **Produced:** The owner manually verified registration, sign-out, and sign-in
  in the local browser. The backend CI checks passed locally: formatting,
  Release build, database migration, all 20 tests, and EF pending-model check.
  Frontend lint, all 5 tests, production build, and production dependency audit
  also passed. The Docker image build had already succeeded.
- **Environment note:** Windows `npm ci` encountered locked native dependency
  files while the app was running. The clean frontend checks passed in an
  isolated temporary copy, and local dependencies were restored afterward.
  No dependency manifest changes were made. GitHub Actions has not run because
  the changes are still uncommitted.

## 2026-11-03 — Sprint 2 owner-only document API

- **Asked:** Treat the owner as a beginner who can perform guided manual work,
  explain whether Sprint 2 can start without manual prerequisites, then build
  the planned first document-security slice.
- **Produced:** Added authenticated PDF upload, owner-scoped list/metadata/
  download endpoints, generated private filesystem keys, size/extension/
  signature checks, and transaction-serialized SHA-256 chained audit events.
  Added API/storage tests and updated API, architecture, threat, backlog, and
  setup documentation. The storage provider is explicitly local-only; the
  audit chain is not claimed to be tamper-proof.
- **What needed correction:** The first test run could not complete its
  database-backed cases because Docker Desktop/PostgreSQL was unavailable.
  A focused storage test then caught that the test attempted to delete an open
  file; the test was corrected to close its read stream before deletion.
- **What I did instead:** Initially, the database-backed tests could not run
  because Docker Desktop was unavailable. After the owner started Docker
  Desktop, migrations succeeded. The first full run exposed that the shared
  test server exhausted the production-sized per-IP auth rate limit across
  many test cases; added a configurable permit limit and raised it only in the
  test host. The full solution test run then passed all 28 tests. The solution
  builds without warnings, .NET format verification passes, and EF reports no
  pending model changes. No AWS resources or real documents were used.
- **Commit:** Pending; record the commit reference after the changes are
  committed.

## 2026-11-03 — Angular document screen

- **Asked:** Add the owner-only upload, list, and download features to the UI.
- **Produced:** Added a signed-in Documents panel for PDF upload, list refresh,
  and download. It shows loading/success/error feedback and rejects non-PDF
  extensions and files over 10 MiB before sending them. Added Angular tests for
  auth headers, listing, upload, download, size rejection, errors, and signed-in
  visibility. Updated local-use instructions and sprint/security notes.
- **What needed correction:** The first test run found a test assertion expected
  different wording than the component's real validation message. The first
  build also exceeded the existing 2 KiB per-component style budget.
- **What I did instead:** Corrected the assertion and moved shared form-message
  styles into the global stylesheet. Frontend lint, all 11 tests, and
  production build now pass without style-budget warnings.
- **Commit:** Pending; record the commit reference after the changes are
  committed.

## 2026-11-03 — Named-user sharing and revocation

- **Asked:** Continue building TrustVault after the owner-only document screen,
  adding named-user sharing and revocation in both the API and UI.
- **Produced:** Added owner-only grant listing/creation/revocation endpoints.
  Active grants authorize a user's document list, metadata, and downloads;
  revocation is checked live and preserved in grant history. Added Angular
  controls for sharing with an existing account, selecting READ/EDIT, viewing
  grants, and revoking access. Updated API, architecture, threat, backlog, and
  beginner test instructions. EDIT currently permits the same read/download
  operations as READ; editing is not implemented.
- **Validation:** All 32 backend tests and all 13 frontend tests pass, and
  frontend lint and .NET formatting verification pass. The production build
  passes; manual two-account verification remains.
- **Commit:** Pending; record the commit reference after the changes are
  committed.

## 2026-11-03 — Fixing grant-list failure

- **Reported:** The owner saw an access error after choosing **Manage access**,
  could grant access, but could not see active grants or revoke access.
- **Cause and fix:** The API's active-grant listing ordered by a property on a
  constructed response record. EF Core could not translate that query and
  returned HTTP 500. Ordered the database projection before constructing the
  response DTO, and added API and UI regression assertions that the grant
  appears with its **Revoke** control.
- **Validation:** All 32 backend tests, all 13 frontend tests, and .NET format
  verification pass.

## 2026-11-04 — Administrator-managed member accounts

- **Asked:** Read the original TrustVault brief and implement a sole
  Administrator role that adds/removes members, with no public account creation.
- **Produced:** Disabled public registration, bootstrapped one Administrator
  from protected configuration, added Administrator-only member APIs/UI,
  Viewer-default member creation (document ownership is per document),
  one-time temporary passwords with forced
  change, account deactivation, immediate access/refresh-token revocation,
  grant revocation, and account lifecycle audit events.
- **What needed correction:** The first integration run exhausted the test
  host's 100-request authentication limit because new member setup correctly
  goes through admin login for each test. Three API tests were rate-limited;
  this was test configuration, not a production limit or authorization failure.
- **What I did instead:** Raised the test-only authentication limit to 1000 and
  reran the isolated PostgreSQL-backed suite; all 72 backend tests pass.
  Solution build, EF migration consistency, frontend lint, all 20 frontend tests,
  production frontend build, and `git diff --check` pass. The existing local
  database was not changed; tests used a separately created temporary database.
- **Commit:** Pending; record the commit reference after the changes are
  committed.

## 2026-11-05 — Administrator oversight console

- **Asked:** Implement the Administrator-focused workflows from the supplied
  use-case details while preserving per-document ownership and preventing
  automatic Administrator access to document contents.
- **Produced:** Added admin-only dashboard, audit investigation/export/chain
  verification, heuristic alerts, metadata and permission review, named-share
  search/revocation, ownership reassignment, credential reset, and read-only
  operational settings, with a matching Angular console and authorization/
  metadata-boundary integration tests.
- **What needed correction:** An early integration test run used the fixture's
  default local database credentials instead of an isolated test database.
  The new administrator endpoints also needed paginated response wrappers to
  match the client contract; the test deserialization was updated accordingly.
- **What I did instead:** Ran the 12 administrator integration tests against
  the isolated `trustvault_admin_tests` database. All 12 passed. The full
  backend suite passed 76 tests but exposed two database-role tests expecting
  PostgreSQL permission error `42501` while the existing audit mutation trigger
  returns `P0001`; neither test exercises the new feature. The solution build,
  frontend lint, all 26 frontend tests, and production frontend build pass.
  These changes remain uncommitted; add the actual commit link after commit.
