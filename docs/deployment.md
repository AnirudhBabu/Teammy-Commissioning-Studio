# Deployment guide

The repository provides a local development host and an opt-in Azure deployment. No cloud resources are created by a build, test, or `terraform validate`. The Azure instructions below require your own subscription, identity provider, registry permissions and protected Terraform backend. They create billable resources.

## Configuration contract

| Setting | Meaning |
| --- | --- |
| `Database__Provider` | `Sqlite` locally or `PostgreSql` for deployment |
| `ConnectionStrings__Workspace` | External connection string; never commit it |
| `Database__Initialize` | Automatic migration/seed only in Development or Testing; production rejects `true` |
| `Database__RuntimePassword` | Migration-job-only optional password for provisioning fixed PostgreSQL role `teammy_runtime`; minimum 32 characters |
| `Authentication__DemoEnabled` | Explicit local/test role-header authentication; production rejects `true` |
| `Authentication__Authority` | HTTPS OIDC authority whose signing keys validate tokens |
| `Authentication__Audience` | Exact audience of API access tokens |
| `Authentication__RoleClaimType` | Defaults to `roles`; signed role values are `Viewer`, `Engineer`, `Approver` |
| `Authentication__MemberMappings__N__Subject` | Trusted issuer's subject mapped to a seeded member |
| `Authentication__MemberMappings__N__MemberId` | `engineer-anirudh`, `approver-elena` or `viewer-guest` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional reachable collector URL; enables trace/metric export |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Set `http/protobuf` for the supplied collector example |
| `TEAMMY_API_TOKEN` | Desktop-only, externally obtained short-lived bearer token; held in process memory |

Every new database is initialized with the same synthetic reference lab. Hosted mode is still a commissioning simulation. No historical MongoDB data is read or imported.

## Local PostgreSQL

`bash scripts/dev-up.sh` creates `.env` with a random local password, builds the API, waits for PostgreSQL, runs `--migrate`, and starts the host on `127.0.0.1:5080`. The Compose configuration deliberately enables development demo authentication and is not the production configuration.

For a production connection, run schema changes explicitly under the migration-owner identity before starting the API:

```bash
# Supply the owner connection and, for PostgreSQL role provisioning, the runtime
# password through a secure environment/secret manager. Do not put them in this command.
dotnet Teammy.Api.dll --migrate
```

The ordinary API must receive the runtime connection, not the owner connection. Keep migrations backward-compatible with the previous application revision.

## Azure infrastructure layout

`deploy/terraform/platform` owns the network, private PostgreSQL 17 server, reference database, ACR, Key Vault, separate runtime/migration managed identities, logs, Container Apps environment, and migration job. `deploy/terraform/service` owns the hosted API and identity configuration.

The PostgreSQL and Key Vault resources have destructive-change protection. Defaults use a cost-conscious, single-zone lab configuration. For an availability commitment, use a compatible General Purpose database SKU, enable `database_high_availability`, review environment zone redundancy, capacity-test, and establish backup/restore objectives.

Terraform creates passwords with cryptographic randomness and stores them through Key Vault secret references. Generated passwords remain in Terraform state. Use an encrypted Azure Storage backend with Azure AD authentication, restricted state access, blob versioning and locking. The example backend file names existing state resources; it does not create or secure them for you. Use distinct state keys for platform and service.

### 1. Authenticate and initialize the platform state

Use Azure CLI sign-in for an interactive bootstrap, or GitHub OIDC for a controlled infrastructure pipeline. Set `ARM_SUBSCRIPTION_ID` to the intended subscription. Register the required Azure resource providers according to your organization's provisioning policy. The infrastructure principal needs resource-creation and role-assignment permissions; it is distinct from the narrower release identity.

```bash
terraform -chdir=deploy/terraform/platform init \
  -backend-config=../backend.hcl.example \
  -backend-config=key=teammy-platform.tfstate \
  -backend-config=use_oidc=false
terraform -chdir=deploy/terraform/platform plan -out=platform.plan
terraform -chdir=deploy/terraform/platform apply platform.plan
```

Replace the example backend resource names first. `use_oidc=false` uses your interactive Azure identity; use `true` in a properly configured OIDC runner. Keep plans out of source control because they can contain secret material. The first apply leaves `create_migration_job=false`, so no unavailable bootstrap image is required.

### 2. Build the first image into the newly created registry

```bash
mkdir -p artifacts
registry=$(terraform -chdir=deploy/terraform/platform output -raw registry_login_server)
registry_name=$(terraform -chdir=deploy/terraform/platform output -raw registry_name)
az acr login --name "$registry_name"
docker buildx build --platform linux/amd64 --push --provenance=mode=max --sbom=true \
  --tag "$registry/teammy-api:bootstrap" \
  --metadata-file artifacts/bootstrap-image.json .
digest=$(python3 -c 'import json; print(json.load(open("artifacts/bootstrap-image.json"))["containerimage.digest"])')
image="$registry/teammy-api@$digest"
```

Run the source/test/container gates before using this image. A tag helps humans find it; the deployment input is the immutable digest.

### 3. Create and run the migration job

```bash
terraform -chdir=deploy/terraform/platform apply \
  -var=create_migration_job=true -var="api_image=$image"
resource_group=$(terraform -chdir=deploy/terraform/platform output -raw resource_group_name)
migration_job=$(terraform -chdir=deploy/terraform/platform output -raw migration_job_name)
bash scripts/run-migration.sh "$resource_group" "$migration_job" "$image"
```

Persist `create_migration_job=true` and the bootstrap digest in your private platform variable file for subsequent infrastructure runs. The job uses the schema owner and provisions the separate runtime role. It runs inside the private network. Key Vault RBAC propagation can briefly delay the first deployment; investigate the actual identity/role scope rather than adding broad permissions.

### 4. Configure identities and create the service

Create/register the API in your identity provider and define the three application roles. Obtain the three real subjects from correctly issued API access tokens. Do not infer a subject from a display name or email. The engineer, approver and viewer subjects must be distinct and their signed roles must match their configured membership.

```bash
terraform -chdir=deploy/terraform/platform output -json service_inputs \
  > deploy/terraform/service/platform.auto.tfvars.json
terraform -chdir=deploy/terraform/service init \
  -backend-config=../backend.hcl.example \
  -backend-config=key=teammy-service.tfstate \
  -backend-config=use_oidc=false
```

The generated platform input file contains resource IDs and a Key Vault URI, not password values. Create a separate ignored local variable file for `api_image`, `oidc_authority`, `oidc_audience`, and `member_subjects`:

```hcl
# Replace each value using your own issuer and the digest already migrated.
api_image      = "yourregistry.azurecr.io/teammy-api@sha256:YOUR_VERIFIED_64_HEX_DIGEST"
oidc_authority = "https://your-configured-issuer.example"
oidc_audience  = "your-api-audience"
member_subjects = {
  engineer = "verified-engineer-subject"
  approver = "verified-independent-approver-subject"
  viewer   = "verified-viewer-subject"
}
```

```bash
terraform -chdir=deploy/terraform/service plan -out=service.plan
terraform -chdir=deploy/terraform/service apply service.plan
terraform -chdir=deploy/terraform/service output -raw api_url
```

Verify readiness, rejected anonymous access, and each role with real tokens. The local demo header must not authenticate the production host. The desktop's `--api` mode requires HTTPS for non-loopback hosts.

## Subsequent releases through GitHub Actions

The included workflow is manual. Publishing and deployment remain under your control; it does not run on ordinary commits.

Configure a GitHub environment named `production` with required reviewers and a main/master branch restriction. Create a federated credential whose exact subject is `repo:OWNER/REPOSITORY:environment:production` and audience is `api://AzureADTokenExchange`. Give that release identity only registry push and the required Container Apps application/job operations at the corresponding resource scopes; it does not need Key Vault Secrets Officer or subscription-owner access.

Set these environment variables in GitHub:

| Variable | Value source |
| --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Federated release identity and intended subscription |
| `ACR_LOGIN_SERVER` | Platform `registry_login_server` output |
| `AZURE_RESOURCE_GROUP` | Platform `resource_group_name` output |
| `MIGRATION_JOB_NAME` | Platform `migration_job_name` output |
| `CONTAINER_APP_NAME` | Service `container_app_name` output |

Dispatch **Deploy to Azure** on the reviewed main/master commit. It runs the reusable verification workflow first, then obtains an OIDC token, builds/pushes the image with provenance/SBOM, scans the exact digest, migrates, pins the serving revision, checks the candidate, moves traffic, and checks the public endpoint. The deployment record retains the digest and both revision names.

The gate checks health and the anonymous-access boundary. It is not a load test or a full authenticated production journey. Add organization-owned authenticated synthetic checks when a safe service identity and workload policy are available.

## Distribution and code signing

The Windows CI artifact is a self-contained `win-x64` desktop package, with actual rendered screenshots from the smoke test. It is unsigned. To distribute broadly, apply your organization's trusted Authenticode signing and update channel to the verified package; do not reuse the old checked-in temporary certificate. Signing keys belong in an appropriate signing service or protected hardware-backed store.

## References

- [Azure login with OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [Container Apps secret references](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets)
- [Container Apps jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Container Apps blue/green revisions](https://learn.microsoft.com/en-us/azure/container-apps/blue-green-deployment?pivots=azure-cli)
