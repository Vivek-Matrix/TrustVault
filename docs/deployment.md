# AWS deployment status

## Hosting decision

AWS is the selected cloud provider for a future internet deployment. The AWS
experience rules are present in `.github/copilot-instructions.md`. TrustVault
has not been deployed to AWS and no AWS application, database, or storage
resources have been created.

The previous Render setup instructions are superseded. Do not create Render
services by following earlier copies of this plan.

## Current state

- The root Dockerfile packages the Angular frontend and ASP.NET Core API into a
  single container image.
- Word document editing uses a separate LibreOffice converter image. The
  converter must run as a non-root service on a private network, without
  database credentials or document-storage mounts, and with strict CPU,
  memory, process, and temporary-storage limits.
- GitHub Actions builds and tests the project and builds the container image.
  It does not publish the image to a registry or deploy it to AWS.
- The API exposes an anonymous readiness probe at `/api/system/ready` for a
  load balancer or container orchestrator. It returns only a ready/not-ready
  status and HTTP 503 when PostgreSQL cannot be reached.
- The backend uses PostgreSQL. AWS database selection and network design have
  not been completed.
- Sprint 4 now separates the PostgreSQL migrator and runtime roles in local
  setup and CI. The production database and secret-injection design are not
  configured.
- The selected Region is `ap-southeast-2`. The `default` AWS CLI profile is
  authenticated and its STS identity check succeeded.
- The AWS CLI v2 and `uv` are available locally, and the Agent Toolkit skill
  setup completed. Its generated `aws-mcp` servers in Claude Code, Cursor, and
  Gemini CLI include both the existing `trustvault-dev` profile and `default`.
  The workspace `.vscode/mcp.json` remote HTTP server was preserved unchanged.
- The AWS project is on the `FREE` plan with $100 in remaining credits. Credits
  do not set a monthly spend limit. The owner selected an AWS-generated
  hostname and asked to see an estimate before choosing a recurring monthly
  budget. No resources may be provisioned until the complete estimate is
  reviewed and approved.
- The owner prefers the more security-focused document design: private S3
  quarantine with asynchronous scanning. This is a design preference, not an
  implemented pipeline or approval to provision AWS resources.
- The local Compose PostgreSQL service is healthy, and the EF migrator
  successfully applied migrations to its existing database. The named database
  volume was preserved. The migrator also succeeded against a separate empty
  PostgreSQL database, which was removed after verification.
- The owner confirmed the Sprint 4 CI workflow succeeded. Current Sprint 5
  implementation changes are local and have not yet been validated by GitHub
  Actions.

## AWS Agent Toolkit setup

The `default` profile is configured for `ap-southeast-2`; browser sign-in and
the STS identity check succeeded. AWS CLI sign-in credentials are temporary.
They are valid for 12 hours and can be renewed for 90 days without repeating
browser authentication. Never request or put AWS access keys or secret keys in
chat or source control.

The verified profile commands were:

```powershell
aws configure set region ap-southeast-2 --profile default
aws login --region ap-southeast-2 --profile default
aws sts get-caller-identity --profile default
aws agent-toolkit list-available-skills --region us-east-1 --profile default
```

The Agent Toolkit setup command completed:

```powershell
aws configure agent-toolkit --yes --region us-east-1 --profile default
```

The wizard installed the default AWS skills and confirmed the existing local
MCP server configurations. The `AWS_MCP_PROXY_PROFILES` value in the generated
local `aws-mcp` entries is `trustvault-dev default`; the existing workspace
remote HTTP entry was not changed. The Toolkit management command uses
`us-east-1` per the setup guide; this does not change the application's selected
Region. All Regional application resources must remain in `ap-southeast-2`.
See the
[official Agent Toolkit setup guide](https://raw.githubusercontent.com/aws/agent-toolkit-for-aws/refs/heads/main/setup-instructions/setup.md).

## Before deployment work

1. In AWS Settings, confirm the project's selected Region is `ap-southeast-2`
   and review Billing, the remaining credit balance, and the project spend
   limit. No recurring monthly budget has been selected yet. New AWS experience
   projects must use their assigned Region for Regional resources.
2. Verify service availability and eligibility for this project before choosing
   services. An active AWS subscription or an installed toolkit does not imply
   that a service is free or enabled.
3. Design an AWS deployment that supports the existing .NET Docker container and
   PostgreSQL. Include container hosting, image storage, database, networking,
   secrets, logs, health checks, a private isolated Word-converter service, and
   a controlled migration step. The requested document pipeline is private S3
   quarantine with asynchronous scanning. Review the complete expected cost
   before provisioning.
4. Do not put database credentials, signing keys, or AWS credentials in Git,
   Docker build arguments, frontend files, or chat. Use an AWS secret-management
   service if a deployment is approved.
5. Do not deploy confidential documents. Local login/token issuance,
   administrator-managed member creation/deactivation, immediate token
   invalidation, document authorization, revocation, and hash-chained audit
   writes are implemented. The API requires a protected
   `AdminBootstrap:Email` and `AdminBootstrap:Password` secret only when no
   administrator exists. Local document scans use ClamAV and block access
   unless clean, but the local filesystem and scanner are not production
   providers. Independent audit anchoring, email verification, password
   recovery, and production database-role configuration remain unfinished.

## Production document pipeline design gate

The local implementation is not a production storage/scanner decision. Before
provisioning production resources, review and approve a service-specific design
and full cost estimate. The intended security behavior is:

- Store uploads under opaque keys in private durable storage with a quarantine
  area and no public access or direct client URLs.
- Record `PENDING_SCAN`; an isolated scanner reads quarantined content and
  reports a terminal `CLEAN`, `REJECTED`, or `SCAN_FAILED` result.
- Only `CLEAN` current documents and versions can be downloaded or edited.
  Keep rejected/failed content quarantined and support safe, auditable retries.
- Serve clean content only through the API after live owner/grant authorization.
  Keep scanning and object-storage credentials least-privileged and separate
  from the API runtime and database migrator.
- Implement and test durable storage, scanner service/isolation, retry/queue
  behavior, retention/cleanup, observability, and backup/restore together. No
  AWS storage, scanning, or hosting resources have been provisioned; the
  service-specific design remains pending.

## Security-readiness roadmap

The implementation sequence and acceptance goals are tracked in the
[project backlog](./backlog.md#remaining-gap-roadmap). The release gates are:

1. Complete Sprint 4 and verify the API role cannot perform schema DDL or
   mutate audit records.
2. Approve and implement private durable document storage and a fail-closed
   malware scanning/quarantine flow; test it with dummy files. The current
   local ClamAV/filesystem pipeline is not the production design.
3. Complete remaining account-lifecycle controls (email verification and
   password recovery) and establish independent audit-chain anchoring.
4. Approve an architecture and cost estimate for `ap-southeast-2`, after
   confirming project spend limits and service eligibility in AWS Settings.
5. Deploy and exercise operational controls only after the security gates pass.

No AWS resource provisioning is part of this sprint or approved by this
roadmap alone.

## Deployment acceptance checks

Once an AWS design is approved and implemented, verify all of the following
before marking internet deployment complete:

- CI builds, tests, and publishes the exact commit intended for deployment.
- The deployed service runs in `ap-southeast-2` and serves the Angular app and
  API over HTTPS.
- The Word-converter service is not publicly reachable, has no database
  credentials or document-storage access, is connected to the API only over a
  private network, and is constrained by container resource limits. Do not
  deploy the loopback-only local development relay.
- Database credentials are stored outside the image and source repository; the
  controlled EF migrator succeeds before the API release starts.
- The configured health check returns success, and service logs show no startup
  or database connection errors.
- Database backup/restore and service cleanup procedures are understood.
- No confidential files are uploaded until the document security work is
  complete and tested.
