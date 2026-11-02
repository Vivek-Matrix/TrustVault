# TrustVault threat notes

This is the current threat baseline, not a claim that the full document product
is secure. The initial Sprint 1 increment adds registration and login, but no
document binaries or ACL endpoints exist yet.

| Threat | Current control | Status / next validation |
|---|---|---|
| Anonymous access to an accidentally added API route | Global authenticated-user fallback policy; explicit anonymous metadata only for health/OpenAPI | Covered for the protected smoke route by an integration test. Add denial tests for every document operation as those routes are built. |
| Forged, expired, wrong-issuer, or wrong-audience bearer token | JWT signature, issuer, audience, and lifetime validation | Valid-token and anonymous smoke tests exist; negative token variants are not covered yet. |
| Guessing another document's identifier (IDOR) | No document API exists; planned per-request server-side ACL lookup | Not mitigated until each document route and file stream is implemented and tested against cross-user access. |
| Revoked access remains usable | No download or share link exists | Design requires current grant lookup per request; no storage or revocation implementation yet. Do not issue public/SAS URLs in the interim. |
| Client-supplied path or storage key exposes files | No file storage endpoint exists; model stores opaque key | Upload validation, generated keys, private container, and authorized streaming remain unimplemented. |
| Audit records are altered or removed | EF rejects audit updates/deletes and a PostgreSQL trigger blocks them; entity contains hash-chain fields | Not tamper-resistant yet: no writer/chain verification and the local migration role owns the schema, so production needs a separate restricted runtime DB role. |
| Malicious or oversized upload | No upload endpoint exists | Extension/MIME/size/content validation, malware scanning, and quotas remain open. |
| Password spraying/account takeover | ASP.NET Core Identity password policy and lockout; authentication endpoints limited to 10 requests/minute per source IP | Rate limiting is in-process and uses the observed remote IP; configure trusted proxy forwarding and an edge limiter before deployment. Failed login attempts are logged but not yet written to the audit trail. MFA and account suspension enforcement remain unimplemented. |
| Secret exposure from source or browser bundle | JWT secret is configuration-only; Angular contains only a public API base URL | AWS application secrets and narrowly scoped storage credentials are not configured. |
| Vulnerable build/test tool dependency | Production `npm audit --omit=dev` reports no vulnerabilities | Full audit still reports moderate transitive development-tool advisories (including glob/minimatch/uuid); review and update before a production CI/deployment setup. |
| Cross-site request abuse | API currently uses bearer tokens and configured CORS origin | No refresh-cookie flow exists; if added, use secure HttpOnly cookies and CSRF defenses. CORS is not an authorization control. |

## Accepted Sprint 0 limitations

- There is no live deployment, production database role separation, AWS object
  storage, refresh-token flow, seeded demo account, or user-facing document flow.
- Hash fields in the schema alone provide no tamper evidence. Sprint 4 must add
  transactional hash generation/verification and a restricted production DB
  role before audit is advertised as tamper-resistant.
- Local Compose credentials are deliberately disposable and must never be reused
  outside a developer machine.
