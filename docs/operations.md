# Operations runbook

## Service signals

| Signal | Meaning | First response |
| --- | --- | --- |
| `/health/live` returns 200 | The HTTP process responds | Check readiness before sending business traffic |
| `/health/ready` returns 503 | The workspace/database is unavailable or uninitialized | Check migration-job result, connection/identity, private DNS and database availability |
| 401 | No valid token for this issuer/audience/lifetime | Obtain a fresh correctly scoped token; never enable demo auth to repair production login |
| 403 | Role, membership or independent-review rule denies the action | Check verified role/subject mapping and project membership; retain the denial |
| 409 | Stale workspace revision or conflicting state | Refresh the workspace and deliberately reapply the intended change |
| Validation error | A command or operating value violates a rule | Show the actionable detail to the engineer; do not suppress it or forge evidence |
| Elevated 5xx or latency | Server/database/host failure | Correlate trace ID, command outcome, revision, and deployment digest |

The API emits JSON console logs, ASP.NET Core traces/metrics, and a `Teammy.Workspace` activity source/meter. Custom instruments are `teammy.workspace.commands` and `teammy.workspace.command.duration`, tagged by command and outcome. `OTEL_EXPORTER_OTLP_ENDPOINT` enables export to a collector. Health probes are filtered from trace creation to limit noise.

The collector configuration under `deploy/observability` illustrates local metric export and trace inspection. Prometheus alert rules target exported ASP.NET duration counters/histograms. Verify metric names and labels against your collector's translation settings before connecting a production pager. No alert notification destination is preconfigured.

## Rollout failure and rollback

The release script records the currently serving revision before changing it. Migration failure stops the release before traffic moves. Candidate failures restore the previous traffic weight and deactivate the unsuccessful candidate. Both database and application versions must remain compatible during this window.

For a manual traffic rollback, identify the known good revision in `artifacts/deployment.json` or the release artifact, then use the exact names from your environment:

```bash
az containerapp revision list --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$CONTAINER_APP_NAME" --output table
az containerapp revision activate --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$CONTAINER_APP_NAME" --revision "$KNOWN_GOOD_REVISION"
az containerapp ingress traffic set --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$CONTAINER_APP_NAME" --revision-weight "$KNOWN_GOOD_REVISION=100"
```

Verify readiness and an authenticated read/write workflow. Traffic rollback does not reverse a schema migration or restore deleted data. Expand/contract schema changes are required so the previous release can still run; do not attempt an improvised destructive down-migration during an incident.

Keep the previous known good revision available during the observation window. After acceptance, deactivate older unused revisions so their minimum replicas do not continue incurring cost. Keep image digests and migration evidence under the agreed retention policy.

## Database recovery

The supplied Azure database config enables a 14-day managed backup retention window and prevents accidental Terraform destruction. It does not by itself prove recoverability. Establish recovery time and recovery point targets, then rehearse a point-in-time restore into a separate server. Check workspace revision consistency, audit counts, role permissions, schema version, and an independent approval journey before changing the service's Key Vault connection reference.

Never restore over the only copy of the current database as a first diagnostic step. Retain the incident snapshot and the rollback decision. After changing a Key Vault secret, restart or revise the application as required for secret refresh and verify which connection it is actually using.

The local Compose volume is development persistence, not a managed backup. `docker compose down --volumes` removes it. Back up a needed lab before deleting that volume.

## Identity and secret rotation

An account/role change belongs in the identity provider and the reviewed subject/member mapping. The desktop token is short-lived and process-local; restart it with a newly issued token after a role or identity change.

To rotate the runtime database password, generate a new value through the protected infrastructure workflow, update the runtime password/connection secret, execute the owner migration/provisioning job to apply the password, then restart the API with the updated runtime secret. Coordinate the change to avoid an old-password/new-password outage; use a second role for a future zero-downtime rotation design. Verify the API still cannot update/delete audit rows or execute DDL. Do not grant it the migration owner's permissions as a workaround.

The historical MongoDB credentials are a separate incident. Their revocation is owned by the original credential owner and is not performed by the new service or its tests; see `SECURITY.md`.

## Capacity and SLOs

The small workspace serializes changes on one aggregate revision. Horizontal replicas improve host availability and read capacity, but do not eliminate write conflicts. Watch conflict rate separately from server-error rate. Large telemetry histories require an explicit retention/archive design before scaling to many devices.

Use the supplied 99.9% availability burn-rate expressions as a starting policy, not a measured availability claim. A 30-day error budget with fast 14.4x and slower 6x burn windows makes sustained errors visible. Calibrate latency budgets and alert volume using a real workload, route grouping, and maintenance policy. Record the test that proves the notification path works before depending on it for on-call response.

## Incident evidence

Capture the commit/image digest, active revision names, UTC incident window, trace IDs, readiness failures, schema version, and relevant audit revisions. Exclude tokens, passwords and connection strings. Explain whether the problem affected authentication, command validation, persistence, simulation evidence, or release approval. Add a regression test at the responsible boundary after recovery.
