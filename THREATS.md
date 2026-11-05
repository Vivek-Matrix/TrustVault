# TrustVault threat notes

This is the current threat baseline, not a claim that the full document product
is secure. The local prototype supports PDF, TXT, DOC, and DOCX uploads,
listing, metadata/download, PDF editing, and named-user grants with immediate
revocation checks. Production storage and tamper-proof audit evidence are not
yet implemented.

| Threat | Current control | Status / next validation |
|---|---|---|
| Anonymous access to an accidentally added API route | Global authenticated-user fallback policy; explicit anonymous metadata only for health/readiness and development OpenAPI. Readiness exposes only whether PostgreSQL is reachable. | Covered for the protected smoke route by an integration test. Add denial tests for every document operation as those routes are built. |
| Forged, expired, wrong-issuer, or wrong-audience bearer token | JWT signature, issuer, audience, and lifetime validation | Valid-token and anonymous smoke tests exist; negative token variants are not covered yet. |
| Guessing another document's identifier (IDOR) | List, metadata, and download require ownership or an active named-user grant; unauthorized and unknown IDs both return 404 | Integration tests cover owner denial, shared read/download, and denied attempts. Run them against local PostgreSQL before relying on the result. |
| Revoked access remains usable | Active grant is checked on each list/metadata/download request; revocation history is retained | Integration tests verify access stops after revocation. Already-downloaded copies cannot be recalled. |
| READ grantee changes a document | Metadata/PDF editing endpoints require the owner or an active EDIT grant; authorization is enforced by the API | Integration tests prove READ cannot update metadata or replace content; keep denial tests for every new edit endpoint. |
| Redacted content remains accessible in the application | Page edits are flattened to image-only PDFs; a redaction save removes previous-version records and attempts to delete those files | Review the output. This does not securely erase filesystem backups or downloaded copies; avoid treating it as certified legal/forensic redaction. |
| Client-supplied path or storage key exposes files | Client cannot set owner/storage key; storage uses generated GUID keys and a filesystem root outside the static web root | Local provider is not production storage; test path traversal and production private-storage configuration before deployment. |
| Audit records are altered or removed | EF rejects audit updates/deletes; PostgreSQL trigger blocks them; writers append a transaction-serialized SHA-256 chain; the migrator can verify record hashes and links | Run `dotnet run --project backend\TrustVault.Migrator -- --verify-audit` against the intended database. This detects altered records and broken/missing links in the stored chain, but not tail deletion or a complete rewrite; external anchoring and restricted runtime credentials remain necessary. |
| Malicious or oversized upload | Upload is limited to 10 MiB, checks extension and format structure, then passes content through the local ClamAV scanner; only `CLEAN` documents/versions can be downloaded or edited. Scanner failures and timeouts fail closed; denied attempts are audited. | Covered by API tests for rejection, scanner failure/retry, and preservation of prior clean content. The local ClamAV and filesystem setup is not production-ready; production storage/scanner design, quotas, and deeper format validation remain open. |
| Malicious Word document exploits document conversion | DOC/DOCX conversion runs in a separate non-root container with no database credentials or document-storage mounts, no published host port, an internal network, and CPU, memory, process, and temporary-storage limits; local API access uses a loopback-only relay | Keep the converter private and patched. Do not mount application secrets or document storage into it. |
| Password spraying/account takeover | ASP.NET Core Identity password policy and lockout; authentication endpoints limited to 10 requests/minute per source IP; public registration is disabled; the sole admin creates members with one-time passwords and mandatory first-login change | Rate limiting is in-process and uses the observed remote IP; configure trusted proxy forwarding and an edge limiter before deployment. Failed login attempts are logged but not yet written to the audit trail. MFA and email verification remain unimplemented. |
| Former member keeps using access after removal | Admin deactivation revokes refresh tokens and rotates the Identity security stamp; JWT validation checks active account state and stamp against PostgreSQL on every request; active grants for or on documents owned by the removed member are revoked | Integration tests verify previously issued access and refresh tokens are rejected. This adds a database lookup to every authenticated request; production availability and database load must be tested. Documents and audit history are retained. |
| Non-admin creates accounts or a caller attempts to set an account role | No public registration route; member management endpoints require the sole Administrator; creation accepts email only and always assigns Viewer, ignoring client-supplied roles; a unique filtered DB index protects the primary-admin marker | API tests cover public registration denial, non-admin denial, and attempts to set Administrator. Bootstrap secrets must be protected and removed after initial setup. |
| Secret exposure from source or browser bundle | JWT secret is configuration-only; Angular contains only a public API base URL | AWS application secrets and narrowly scoped storage credentials are not configured. |
| Vulnerable build/test tool dependency | Production `npm audit --omit=dev` reports no vulnerabilities | Full audit still reports moderate transitive development-tool advisories (including glob/minimatch/uuid); review and update before a production CI/deployment setup. |
| Cross-site request abuse | API currently uses bearer tokens and configured CORS origin | No refresh-cookie flow exists; if added, use secure HttpOnly cookies and CSRF defenses. CORS is not an authorization control. |
| Administrator becomes an unrestricted document reader | Admin APIs return metadata, permission history, and audit fields only; document endpoints still require ownership or an active document grant | Integration tests prove an Administrator cannot download a member document merely by role. Admin document reassignment can intentionally grant a member ownership and revokes existing grants. |
| Abuse of administrator investigation tools | Admin routes require the sole Administrator role; searches are paginated; exports are capped and omit event metadata JSON; permission revocation and ownership reassignment require a reason and append audit events | Review audit access and exported CSV handling operationally. Admin credential delivery remains out-of-band; there is no MFA or notification channel. |
| Security alert false positives or missed activity | Initial heuristics detect >=5 audited denials per actor/IP in 10 minutes and >=20 successful downloads per actor/IP in one hour | These are basic rules, not intrusion detection. Anonymous route failures are not recorded here; device fingerprinting, notifications, configurable thresholds, and rate-window tests remain future work. |

## Accepted Sprint 0 limitations

- There is no live deployment, production database role configuration, AWS
  object storage, or seeded demo account. Refresh-token rotation is implemented
  locally.
- The hash chain can be verified locally with the migrator, but it is not
  anchored outside PostgreSQL. Verification cannot detect tail deletion or a
  complete rewrite by a privileged database operator. Do not advertise the
  audit as tamper-proof; external anchoring and a restricted production DB role
  remain unimplemented.
- Local Compose credentials are deliberately disposable and must never be reused
  outside a developer machine.
- Administrator settings are currently read-only. Alert thresholds are fixed
  heuristics; external sharing means named registered accounts only, with no
  guest links or expiration. Orphan ownership reassignment is supported, but
  quarantine, retention/recovery, deletion approval, MFA, and self-service
  password recovery are not.
