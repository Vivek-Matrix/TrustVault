# TrustVault Testing Guide

A step-by-step guide to testing TrustVault on a Windows PC: automated backend
and frontend tests, code-quality checks, and a manual end-to-end checklist.
It assumes you have finished [SETUP.md](SETUP.md) (Docker services running,
database migrated). Commands come from `.github/workflows/ci.yml`,
`frontend/package.json`, `docker-compose.yml`, and `backend/TrustVault.Tests/`.
Anything I could not verify from code is marked **Needs confirmation**.

## Table of contents

1. [What gets tested](#1-what-gets-tested)
2. [Before you start](#2-before-you-start)
3. [Part A: Automated backend tests](#3-part-a-automated-backend-tests)
4. [Part B: Frontend lint, tests, build](#4-part-b-frontend-lint-tests-build)
5. [Part C: Code-quality and schema checks](#5-part-c-code-quality-and-schema-checks)
6. [Part D: Manual end-to-end checklist](#6-part-d-manual-end-to-end-checklist)
7. [Part E: Optional checks](#7-part-e-optional-checks)
8. [Local test (project README walkthrough)](#8-local-test-project-readme-walkthrough)
9. [Continuous integration](#9-continuous-integration)
10. [Test inventory](#10-test-inventory)
11. [Troubleshooting](#11-troubleshooting)
12. [Gaps and corrections](#12-gaps-and-corrections)

---

## 1. What gets tested

| Layer | Tool | Needs Docker? | Needs ClamAV? |
|---|---|---|---|
| Backend integration and unit tests | xUnit 2.9.2, `Microsoft.AspNetCore.Mvc.Testing` | **PostgreSQL only** | No (fake scanner) |
| Frontend unit tests | Angular `ng test` (Karma) in headless Chrome | No | No |
| Frontend lint | `ng lint` (ESLint) | No | No |
| Formatting | `dotnet format` | No | No |
| Schema drift | `dotnet-ef migrations has-pending-model-changes` | No | No |
| Manual end-to-end | Browser | Yes, all four services | **Yes** |

Why ClamAV is not needed for automated tests: the test host replaces the real
scanner and Word converter with fakes (`TestDocumentScanner`,
`TestDocumentTextConverter` in `ApiAuthorizationTests.cs`). The ClamAV protocol
itself is tested against a fake local TCP server
(`ClamAvPrivateDocumentScannerTests.cs`).

## 2. Before you start

Open PowerShell in the repository root and confirm:

```powershell
git branch --show-current          # expect: feature/TV-001-project-foundation
docker compose ps                  # postgres (and clamav, converter, converter-dev-proxy) running
dotnet --version                   # 10.x
node --version                     # 20.19+ or 22.12+
```

If the containers are not running:

```powershell
docker compose up -d postgres clamav converter converter-dev-proxy
```

The database must already have its tables and role grants. If you have not done
this, follow SETUP.md steps for roles, `dotnet run --project backend\TrustVault.Migrator`,
and the second run of the role script.

> **Important, read this.** By default the backend tests connect to the same
> local database you use for development: `localhost:55432`, database
> `trustvault`, user `trustvault_runtime` (defaults hard-coded in
> `ApiAuthorizationTests.cs`). They create test users and documents there.
> `PROMPTS.md` records using a separate database (`trustvault_admin_tests`)
> for isolated runs. For anything you care about, use an isolated database
> (see [3.2](#32-optional-use-an-isolated-test-database)).

## 3. Part A: Automated backend tests

### 3.1 Run all backend tests

```powershell
dotnet restore TrustVault.sln
dotnet test TrustVault.sln
```

Expected: the run finishes with `Passed!` and no failures. Test counts are in
[section 10](#10-test-inventory); run to see the authoritative total.

Useful variations:

```powershell
# Only one test class
dotnet test TrustVault.sln --filter "FullyQualifiedName~AuditChainVerifierTests"

# Only tests whose name contains a word
dotnet test TrustVault.sln --filter "DisplayName~revoke"

# Show each test name
dotnet test TrustVault.sln --logger "console;verbosity=detailed"
```

### 3.2 Optional: use an isolated test database

The migrator and role script both target the database named `trustvault`
(`setup-roles.sql` grants on `trustvault`), so a fully separate database needs
extra manual SQL. **Needs confirmation:** the exact commands used for
`trustvault_admin_tests` are not in the repo. The mechanism, however, is
documented in the code: set `TRUSTVAULT_TEST_CONNECTION` to a connection string
and the fixture uses it instead of the default.

```powershell
$env:TRUSTVAULT_TEST_CONNECTION = "Host=127.0.0.1;Port=55432;Database=<your_test_db>;Username=trustvault_runtime;Password=runtime_local"
dotnet test TrustVault.sln
Remove-Item Env:TRUSTVAULT_TEST_CONNECTION
```

The test database must already be migrated and have the runtime-role grants.

### 3.3 What the backend tests prove

- Anonymous requests to protected routes are rejected (deny-by-default).
- Valid, expired, and wrongly signed tokens are handled correctly.
- A different account cannot retrieve another owner's document, even with its ID.
- Revoked access to a document ID returns 404.
- Only the Administrator can use admin endpoints, and admin endpoints return
  metadata only, never file bytes.
- Audit hash chains detect modified records and broken links.
- Upload validation, scan-status handling, and scan retry behave as designed.

(Descriptions are inferred from test file and method names and `PROMPTS.md`,
not from a line-by-line review of all 73-or-so tests.)

## 4. Part B: Frontend lint, tests, build

```powershell
Set-Location frontend
npm ci
npm run lint
npm test -- --watch=false --browsers=ChromeHeadless
npm run build
npm audit --omit=dev
Set-Location ..
```

| Command | Passes when |
|---|---|
| `npm run lint` | No ESLint errors |
| `npm test -- --watch=false --browsers=ChromeHeadless` | All Karma specs pass; needs Chrome installed |
| `npm run build` | Production build succeeds within the style budgets in `angular.json` |
| `npm audit --omit=dev` | No known production-dependency vulnerabilities |

Do not run `npm ci` while `npm start` is running; Windows can lock native files
and the install fails (`PROMPTS.md`).

## 5. Part C: Code-quality and schema checks

These are enforced by CI, so run them before you push:

```powershell
dotnet tool restore
dotnet format TrustVault.sln --verify-no-changes
dotnet build TrustVault.sln --configuration Release
dotnet tool run dotnet-ef migrations has-pending-model-changes --project backend/TrustVault.Infrastructure --startup-project backend/TrustVault.Api
```

If formatting fails, run `dotnet format TrustVault.sln` and commit the result.
The EF check needs `Jwt__SigningKey` set (32+ bytes), as in CI.

Audit-chain verification against your current local database:

```powershell
$env:ConnectionStrings__TrustVaultMigrator = "Host=127.0.0.1;Port=55432;Database=trustvault;Username=trustvault_migrator;Password=migrator_local"
dotnet run --project backend\TrustVault.Migrator -- --verify-audit
Remove-Item Env:ConnectionStrings__TrustVaultMigrator
```

Expected: `Audit chain verified: <n> events.` Exit code 1 and a message naming the
first bad event if verification fails (`Migrator/Program.cs`).

## 6. Part D: Manual end-to-end checklist

Start the app as described in SETUP.md (API in Terminal 1, `npm start` in
Terminal 2), then open `http://localhost:4200`. Use **dummy files only**.

First, a quick service check from PowerShell:

```powershell
Invoke-WebRequest http://localhost:5106/api/system/health   # expect 200 {"status":"ok"}
```

`/api/system/ready` is also anonymous in the current code
(`SystemController.cs`) and returns 200 only when PostgreSQL **and** ClamAV
answer. Expect 503 if ClamAV is still starting.

> **Important correction to a common instruction.** Only a document's **owner**
> shares it. The Administrator does **not** grant access. Admin endpoints only
> list, revoke, and reassign (`AdminOversightController.cs`; owner-only grant
> creation in `DocumentsController.cs` and `docs/api.md`). So the sharing test
> below is done by a **member**, not the administrator.

Use two member accounts, Member A and Member B. Use separate browser profiles or
sign out between accounts; the browser keeps the token in memory only.

| # | Steps | Expected result |
|---|---|---|
| D1 | Sign in as the bootstrap administrator | Succeeds; the Admin console and **Manage members** are visible |
| D2 | **Manage members** → add Member A and Member B by email | A one-time temporary password is displayed for each; save it |
| D3 | Sign in as Member A with the temporary password | Forced to change password; other pages blocked until done |
| D4 | Change password (12+ chars, upper, lower, digit, symbol) | Succeeds; you are signed in |
| D5 | Upload a small PDF as Member A | Appears in the list with status `pending`/`scanning`, then `clean` |
| D6 | Click **Download** on the clean file | File downloads |
| D7 | Try a file over 10 MiB, or a `.exe` renamed `.pdf` | Rejected with a validation message |
| D8 | Sign in as Member B (after D3–D4 for B) | Document list is **empty** |
| D9 | As Member A: **Manage access** → enter Member B's email, **Read**, **Grant access** | Grant appears in the list |
| D10 | As Member B: **Refresh list** | Shared document appears with permission `READ`; **Download** works; edit is not allowed |
| D11 | As Member A: give B **Edit** access (revoke B first, then grant Edit, if the UI does not allow changing the level directly; **Needs confirmation**) | B can now edit title/details and edit TXT/Word text or the PDF |
| D12 | As Member A: **Revoke** B's access | B's list no longer shows it; opening the old ID returns 404 |
| D13 | Open the clock icon on a document | History lists upload, reads, grants, revocations with actor and time |
| D14 | Edit a TXT file, save | New version; earlier version under **Previous versions** |
| D15 | Upload a DOCX, edit, save | Saves text only (formatting lost, per known limitation); needs the converter running |
| D16 | PDF editor: add text, reorder/remove a page, save | Saved; the file is flattened to images (known limitation) |
| D17 | Use **Redact area** on a disposable PDF, save, download | Area is black; old version no longer in previous versions |
| D18 | As Member A: delete the document (trash icon, confirm) | Gone for everyone, grants revoked |
| D19 | As the administrator: open the audit view, filter by action/user | Events from the steps above appear |
| D20 | As the administrator: **Verify audit chain** (or `GET /api/admin/audit/verify`) | Reports the chain is valid |
| D21 | As the administrator: remove Member B | B is signed out immediately; cannot sign in; B's documents and audit entries remain |
| D22 | Try 6 wrong passwords for a member | Account locked (5 failures, 15 minutes lockout; `Program.cs`) |
| D23 | Reload the page | You are signed out (token only in memory) |

If you do not need every row, the minimum smoke test is D1, D2, D4, D5, D6,
D9, D10, D12.

## 7. Part E: Optional checks

**Malware rejection.** Upload the standard EICAR antivirus test file
(a harmless text file from eicar.org) saved as `.txt`. Expected: scan status
`rejected`, and no download or edit allowed. This relies on the real ClamAV
service; the result is **Needs confirmation** because it is not covered by
the automated tests.

**ClamAV outage behaviour.** Stop it, upload, and watch the failure:

```powershell
docker compose stop clamav
# upload a file in the UI -> status should become "scan failed"
docker compose start clamav
# wait for clamd to be ready, then choose Retry scan -> "clean"
```

Also, `GET /api/system/ready` returns 503 while ClamAV is stopped. This is the
same behaviour that causes the Railway deploy to be marked unhealthy when the
`clamav` service cannot start ([ARCHITECTURE.md §11](ARCHITECTURE.md#11-readiness-and-health)).

**ClamAV readiness.** ClamAV must finish loading signatures before it answers.
Watch `docker compose logs -f clamav` until it reports readiness. First start
can take a while; the "30 to 90 seconds" figure often quoted is an estimate, not
measured here (**Needs confirmation**).

**Rate limiting.** Send more than 10 login requests in a minute from one IP to
one path; later ones should get HTTP 429 with a "Too many authentication
requests" message (`Program.cs`).

**Swagger.** In Development only: `http://localhost:5106/swagger`.

## 8. Local test (project README walkthrough)

Copied verbatim from the project's `README.md` ("Try the document screen"). Start
the API as described in [SETUP.md](SETUP.md) first. This is the original
walkthrough; the more detailed checklist with expected results is in
[Part D](#6-part-d-manual-end-to-end-checklist).

### Try the document screen

1. Keep the API running in the first PowerShell window.
2. In a second PowerShell window, run `Set-Location frontend` and then
   `npm start`.
3. Open `http://localhost:4200` and sign in as the bootstrap administrator.
4. In **Manage members**, add a member by email. The account receives the
   Viewer base role automatically. Deliver the displayed temporary password
   securely out-of-band. Sign out, then sign in as that member and change the
   temporary password.
5. In **Documents**, enter a title/category, choose a small dummy PDF, TXT,
   DOC, or DOCX file, and select **Upload file**. Wait for the scan status to
   show `clean`; if it shows `scan failed`, confirm the local ClamAV service is
   ready and select **Retry scan**. Only clean files can be downloaded or
   edited. Choose **Edit** to edit TXT or Word
   text, or to open the page editor for a PDF. The converter service must be
   running to edit Word files.
5. Sign out, create a second test account, and confirm its document list is
   empty. Automated API tests separately verify that a different account
   cannot retrieve another owner's document, even with its ID.
6. Sign back in as the owner. On the document, choose **Manage access**, enter
   the second account's email, leave permission as **Read**, then choose
   **Grant access**.
7. Sign out and sign in as the second account. Choose **Refresh list**; the
   shared document should appear with its permission, and **Download** should
   work.
8. Sign back in as the owner, choose **Manage access**, then **Revoke** beside
   the second account and confirm. Sign back in as that account and refresh:
   the document should no longer be listed. The API integration tests also
   verify that revoked access to its old document ID returns 404.
9. Repeat the sharing steps, this time choosing **Edit**. Sign in as the second
   account and choose **Edit** on the shared document. Try saving a new title,
   edit and save a TXT or Word text file, then use a disposable PDF to add text,
   draw, replace an area, reorder/remove a page, and select **Save edited PDF**.
   Replace the PDF with a different dummy PDF and verify the previous file is
   available under **Previous versions**.
10. Use a disposable PDF with non-sensitive sample text to test **Redact area**.
    Save the result, download it, and verify the covered area is black and the
    old file no longer appears in **Previous versions**. Do not treat this as
    secure deletion from backups or already-downloaded copies.
11. Choose the clock icon on a document to view its creation, read, and edit
    history, including who performed the action and when. Owners and users with
    an active grant can view the history; revoked users cannot.
12. As the owner, choose the trash icon to remove a document. Confirm the
    warning to delete it and revoke every user's shared access. The document
    and its previous versions become unavailable to everyone.

Do not use real confidential files. The browser keeps the access token only in
memory; refreshing or closing the page signs the user out. Use separate
browser profiles or sign out between accounts during this test.

> Note: the source README numbers two consecutive steps as "5." (the second
> should read 6, so the sequence really has 13 steps). The text above is kept
> exactly as in the README.

## 9. Continuous integration

`.github/workflows/ci.yml` runs on every `push` and `pull_request`
(`contents: read`).

| Job | Steps |
|---|---|
| `backend` (Ubuntu, Postgres 16 service) | restore, tool restore, format check, Release build, provision restricted roles, configure connection strings, apply migrations, enforce append-only audit privileges, `dotnet test`, EF pending-model-changes check |
| `frontend` (Node 20) | `npm ci`, lint, test (ChromeHeadless), build, `npm audit --omit=dev` |
| `container` | `docker build --tag trustvault:ci .` |

CI does not start ClamAV or the converter and has no deploy step. How Railway
triggers redeploys is **Needs confirmation**.

To reproduce CI locally, run Parts A to C in this guide.

## 10. Test inventory

Counts below are static greps of `[Fact]`/`[Theory]` and `it(` lines, so they
are approximate: `PROMPTS.md` reported 76 backend and 26 frontend tests at one
point, and `[Theory]` rows multiply runtime counts (**Needs confirmation**).

| File | Approx. | Focus (inferred from names) |
|---|---|---|
| `ApiAuthorizationTests.cs` | 50 | Auth, JWT, roles, owner/grant authorization, 404 behaviour, admin boundaries, uploads, member lifecycle |
| `AuditChainVerifierTests.cs` | 7 | Chain verification, tamper and broken-link detection |
| `AuditEventImmutabilityTests.cs` | 1 | Audit rows cannot be changed |
| `ClamAvPrivateDocumentScannerTests.cs` | 4 | `PONG` readiness; `OK` and `FOUND` verdict parsing |
| `DatabaseConnectionStringsTests.cs` | 6 | Config and `postgres://` URL normalisation |
| `DatabaseRolePermissionTests.cs` | 3 | Runtime-role privileges |
| `PrivateDocumentStorageTests.cs` | 2 | Opaque-key storage |
| `admin-oversight.service.spec.ts` | 4 | Admin client service |
| `auth.service.spec.ts` | 5 | Auth service |
| `app.component.spec.ts` | 8 | Shell and signed-in visibility |
| `documents-panel.component.spec.ts` | 13 | Upload, list, download, validation, grants |

## 11. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Nearly all backend tests fail at once | PostgreSQL not running, tables missing (run the migrator), or runtime grants missing (rerun the role script) |
| Error `28P01` (password authentication failed) | Local role password drifted; see [SETUP.md troubleshooting](SETUP.md#7-troubleshooting) |
| HTTP 429 in tests | Rate limit not raised; the fixture sets `RateLimits__AuthenticationPermitLimit` to 1000, so check that no outer environment variable overrides it |
| `has-pending-model-changes` fails | Entity change without a migration; add one |
| `dotnet format` fails | Run `dotnet format TrustVault.sln` and commit |
| Style budget error in frontend build | Move shared styles to `src/styles.scss` |
| `npm ci` file lock errors | Stop `npm start` first |
| ChromeHeadless not found | Install Chrome, or set `CHROME_BIN` (**Needs confirmation** for your setup) |
| Upload stays `pending` or `scan failed` | ClamAV still loading or stopped; check `docker compose logs clamav`, then **Retry scan** |
| Word editing fails | Converter not running, or `DocumentConversion__Endpoint` is wrong |
| Port 55432 or 5106 in use | Another process owns the port; stop it |

## 12. Gaps and corrections

- **Instruction accuracy.** A step in an earlier setup walkthrough said the
  administrator grants READ/EDIT to a member. In the code the owner grants;
  the administrator can revoke and reassign. This guide follows the code.
- **Admin e-mail.** SETUP.md uses `admin@gmail.com`, other guides use
  `admin@example.com`. Any valid address works if it matches
  `AdminBootstrap__Email`.
- **DB role tests.** Two `DatabaseRolePermissionTests` expected PostgreSQL error
  `42501`, but the audit trigger raises `P0001`; they failed in a 2026-11-05 run
  (`PROMPTS.md`). Current status **Needs confirmation**.
- **Default test DB.** Tests default to your development database; an isolated
  database procedure is not documented in the repo.
- **Missing coverage.** No end-to-end browser tests, coverage threshold, or
  automated tests of live ClamAV or LibreOffice were found.
