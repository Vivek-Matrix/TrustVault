# AWS deployment status

## Hosting decision

AWS is the selected cloud provider for a future internet deployment. The AWS
experience rules are present in `.github/copilot-instructions.md`, but the AWS
CLI profile is for local development only. TrustVault has not been deployed to
AWS and no AWS application, database, or storage resources have been created.

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
- The backend uses PostgreSQL. AWS database selection and network design have
  not been completed.
- The requested selected Region is `ap-southeast-2`. The owner confirmed the
  `trustvault-dev` profile; its AWS CLI sign-in succeeded and its configured
  Region is `ap-southeast-2`.
- The AWS CLI v2 and `uv` are available locally, and the Agent Toolkit skill
  catalog returned 119 skills.
- The workspace `.vscode/mcp.json` already contains an AWS MCP remote HTTP
  endpoint. The local Agent Toolkit setup wizard was not run because its
  generated profile-bound server could conflict with that existing connection.
  The existing config was left untouched.
- The owner confirmed the selected Region `ap-southeast-2` and $100.00 in
  remaining credits. Credits may expire or have service restrictions and do
  not impose a recurring monthly spend limit. Confirm the project spend limit,
  service eligibility, and full design cost in AWS Settings before creating
  AWS application resources.
- The local Compose PostgreSQL service is healthy, and the EF migrator
  successfully applied migrations to its existing database. The named database
  volume was preserved. The migrator also succeeded against a separate empty
  PostgreSQL database, which was removed after verification.
- The latest pushed CI run for commit `5db247e` succeeded. Later local edits are
  uncommitted and have not been validated by GitHub Actions.

## AWS Agent Toolkit setup

The owner confirmed the `trustvault-dev` profile. AWS CLI v2 and `uv` are
installed, browser sign-in succeeded, and the STS identity check passed.
Credentials last 12 hours and can be renewed for up to 90 days without repeating
browser authentication. Never request or put AWS access keys or secret keys in
chat or source control.

The verified profile commands were:

```powershell
aws configure set region ap-southeast-2 --profile trustvault-dev
aws login --region ap-southeast-2 --profile trustvault-dev
aws sts get-caller-identity --profile trustvault-dev
aws agent-toolkit list-available-skills --region us-east-1 --profile trustvault-dev
```

The final command returned 119 skills. The setup guide's wizard command is:

```powershell
aws configure agent-toolkit --yes --region us-east-1 --profile trustvault-dev
```

It was not run because the existing workspace `aws-mcp` entry is a remote HTTP
server, not the generated local server configuration that accepts
`AWS_MCP_PROXY_PROFILES`. Do not add that variable to the remote HTTP entry or
overwrite it. If a profile-bound local MCP server is needed later, add it as a
separate server after reviewing the generated configuration. See the
[official Agent Toolkit setup guide](https://raw.githubusercontent.com/aws/agent-toolkit-for-aws/refs/heads/main/setup-instructions/setup.md)
for the remaining configuration steps.

The toolkit management command uses `us-east-1` per the setup guide; this does
not change the application's selected Region. Regional application resources
must remain in `ap-southeast-2`. If the toolkit is configured later, preserve
the current remote HTTP entry and set `AWS_MCP_PROXY_PROFILES=trustvault-dev`
only on the separate generated local server.

## Before deployment work

1. In AWS Settings, confirm the project's selected Region is `ap-southeast-2`
   and review Billing, the remaining credit balance, and the project spend
   limit. New AWS experience projects must use their assigned Region for
   Regional resources.
2. Verify service availability and eligibility for this project before choosing
   services. An active AWS subscription or an installed toolkit does not imply
   that a service is free or enabled.
3. Design an AWS deployment that supports the existing .NET Docker container and
   PostgreSQL. Include container hosting, image storage, database, networking,
   secrets, logs, health checks, a private isolated Word-converter service, and
   a controlled migration step. Review the complete expected cost before
   provisioning.
4. Do not put database credentials, signing keys, or AWS credentials in Git,
   Docker build arguments, frontend files, or chat. Use an AWS secret-management
   service if a deployment is approved.
5. Do not deploy confidential documents. Login/token issuance, document
   authorization and revocation, private object storage, audit-chain writing,
   and the production database-role split are unfinished.

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
