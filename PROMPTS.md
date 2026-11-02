# AI prompt log

This log records actual work as it happens. No future prompts or commits are
invented. The repository has no remote URL yet, so the first commit is identified
by its local hash.

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
