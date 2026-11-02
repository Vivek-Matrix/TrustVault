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
