# TrustVault threat notes

This is the current threat baseline, not a claim that the full document product
is secure. The local prototype supports PDF, TXT, DOC, and DOCX uploads,
listing, metadata/download, PDF editing, and named-user grants with immediate
revocation checks. Production storage and tamper-proof audit evidence are not
yet implemented.

| Threat | Current control | Status / next validation |
|---|---|---|
| Anonymous access to an accidentally added API route | Global authenticated-user fallback policy; explicit anonymous metadata only for health/OpenAPI | Covered for the protected smoke route by an integration test. Add denial tests for every document operation as those routes are built. |
| Forged, expired, wrong-issuer, or wrong-audience bearer token | JWT signature, issuer, audience, and lifetime validation | Valid-token and anonymous smoke tests exist; negative token variants are not covered yet. |
| Guessing another document's identifier (IDOR) | List, metadata, and download require ownership or an active named-user grant; unauthorized and unknown IDs both return 404 | Integration tests cover owner denial, shared read/download, and denied attempts. Run them against local PostgreSQL before relying on the result. |
| Revoked access remains usable | Active grant is checked on each list/metadata/download request; revocation history is retained | Integration tests verify access stops after revocation. Already-downloaded copies cannot be recalled. |
| READ grantee changes a document | Metadata/PDF editing endpoints require the owner or an active EDIT grant; authorization is enforced by the API | Integration tests prove READ cannot update metadata or replace content; keep denial tests for every new edit endpoint. |
| Redacted content remains accessible in the application | Page edits are flattened to image-only PDFs; a redaction save removes previous-version records and attempts to delete those files | Review the output. This does not securely erase filesystem backups or downloaded copies; avoid treating it as certified legal/forensic redaction. |
| Client-supplied path or storage key exposes files | Client cannot set owner/storage key; storage uses generated GUID keys and a filesystem root outside the static web root | Local provider is not production storage; test path traversal and production private-storage configuration before deployment. |
| Audit records are altered or removed | EF rejects audit updates/deletes; PostgreSQL trigger blocks them; writers append a transaction-serialized SHA-256 chain; the migrator can verify record hashes and links | Run `dotnet run --project backend\TrustVault.Migrator -- --verify-audit` against the intended database. This detects altered records and broken/missing links in the stored chain, but not tail deletion or a complete rewrite; external anchoring and restricted runtime credentials remain necessary. |
| Malicious or oversized upload | Upload is limited to 10 MiB and accepts PDF, TXT, DOC, or DOCX after extension and format-specific signature/structure checks | MIME is not trusted. Validation is not malware scanning; scanning, quotas, and deeper format validation remain open. |
| Malicious Word document exploits document conversion | DOC/DOCX conversion runs in a separate non-root container with no database credentials or document-storage mounts, no published host port, an internal network, and CPU, memory, process, and temporary-storage limits; local API access uses a loopback-only relay | Keep the converter private and patched. Do not mount application secrets or document storage into it. |
| Password spraying/account takeover | ASP.NET Core Identity password policy and lockout; authentication endpoints limited to 10 requests/minute per source IP | Rate limiting is in-process and uses the observed remote IP; configure trusted proxy forwarding and an edge limiter before deployment. Failed login attempts are logged but not yet written to the audit trail. MFA and account suspension enforcement remain unimplemented. |
| Secret exposure from source or browser bundle | JWT secret is configuration-only; Angular contains only a public API base URL | AWS application secrets and narrowly scoped storage credentials are not configured. |
| Vulnerable build/test tool dependency | Production `npm audit --omit=dev` reports no vulnerabilities | Full audit still reports moderate transitive development-tool advisories (including glob/minimatch/uuid); review and update before a production CI/deployment setup. |
| Cross-site request abuse | API currently uses bearer tokens and configured CORS origin | No refresh-cookie flow exists; if added, use secure HttpOnly cookies and CSRF defenses. CORS is not an authorization control. |

## Accepted Sprint 0 limitations

- There is no live deployment, production database role separation, AWS object
  storage, refresh-token flow, or seeded demo account.
- The hash chain can be verified locally with the migrator, but it is not
  anchored outside PostgreSQL. Verification cannot detect tail deletion or a
  complete rewrite by a privileged database operator. Do not advertise the
  audit as tamper-proof; external anchoring and a restricted production DB role
  remain unimplemented.
- Local Compose credentials are deliberately disposable and must never be reused
  outside a developer machine.
