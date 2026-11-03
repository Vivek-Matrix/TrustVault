# TrustVault threat notes

This is the current threat baseline, not a claim that the full document product
is secure. The local prototype supports PDF upload, listing, metadata/download,
and named-user grants with immediate revocation checks. Production storage and
tamper-proof audit evidence are not yet implemented.

| Threat | Current control | Status / next validation |
|---|---|---|
| Anonymous access to an accidentally added API route | Global authenticated-user fallback policy; explicit anonymous metadata only for health/OpenAPI | Covered for the protected smoke route by an integration test. Add denial tests for every document operation as those routes are built. |
| Forged, expired, wrong-issuer, or wrong-audience bearer token | JWT signature, issuer, audience, and lifetime validation | Valid-token and anonymous smoke tests exist; negative token variants are not covered yet. |
| Guessing another document's identifier (IDOR) | List, metadata, and download require ownership or an active named-user grant; unauthorized and unknown IDs both return 404 | Integration tests cover owner denial, shared read/download, and denied attempts. Run them against local PostgreSQL before relying on the result. |
| Revoked access remains usable | Active grant is checked on each list/metadata/download request; revocation history is retained | Integration tests verify access stops after revocation. Already-downloaded copies cannot be recalled. |
| Client-supplied path or storage key exposes files | Client cannot set owner/storage key; storage uses generated GUID keys and a filesystem root outside the static web root | Local provider is not production storage; test path traversal and production private-storage configuration before deployment. |
| Audit records are altered or removed | EF rejects audit updates/deletes; PostgreSQL trigger blocks them; writers append a transaction-serialized SHA-256 chain | Chain verification and external anchoring are absent; schema-owning database credentials can bypass the trigger or rewrite history. Not tamper-proof; production needs a restricted runtime role and verification. |
| Malicious or oversized upload | Upload is limited to 10 MiB, requires `.pdf`, and checks the `%PDF-` signature before storage | This is not full PDF parsing or malware detection. MIME is not trusted; scanning, quotas, and stronger content validation remain open. |
| Password spraying/account takeover | ASP.NET Core Identity password policy and lockout; authentication endpoints limited to 10 requests/minute per source IP | Rate limiting is in-process and uses the observed remote IP; configure trusted proxy forwarding and an edge limiter before deployment. Failed login attempts are logged but not yet written to the audit trail. MFA and account suspension enforcement remain unimplemented. |
| Secret exposure from source or browser bundle | JWT secret is configuration-only; Angular contains only a public API base URL | AWS application secrets and narrowly scoped storage credentials are not configured. |
| Vulnerable build/test tool dependency | Production `npm audit --omit=dev` reports no vulnerabilities | Full audit still reports moderate transitive development-tool advisories (including glob/minimatch/uuid); review and update before a production CI/deployment setup. |
| Cross-site request abuse | API currently uses bearer tokens and configured CORS origin | No refresh-cookie flow exists; if added, use secure HttpOnly cookies and CSRF defenses. CORS is not an authorization control. |

## Accepted Sprint 0 limitations

- There is no live deployment, production database role separation, AWS object
  storage, refresh-token flow, or seeded demo account. `EDIT` grants currently
  provide read/download access only; no document editing endpoint exists.
- The hash chain is written transactionally but is not yet verified or anchored
  outside PostgreSQL. Do not advertise the audit as tamper-proof until those
  controls and a restricted production DB role are implemented.
- Local Compose credentials are deliberately disposable and must never be reused
  outside a developer machine.
