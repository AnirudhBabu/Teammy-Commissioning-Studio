#!/usr/bin/env bash
# Blue/green rollout with pre-deploy migration, isolated readiness check, and traffic rollback.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -ne 2 ]]; then
  echo 'Usage: scripts/deploy-azure.sh IMAGE@sha256:DIGEST REVISION_SUFFIX' >&2
  exit 2
fi
: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP}"
: "${CONTAINER_APP_NAME:?Set CONTAINER_APP_NAME}"
: "${MIGRATION_JOB_NAME:?Set MIGRATION_JOB_NAME}"
image=$1
suffix=$2
[[ "$image" =~ ^[a-z0-9.-]+/[a-z0-9/_.-]+@sha256:[a-f0-9]{64}$ ]] || { echo 'Invalid image digest.' >&2; exit 2; }
[[ "$suffix" =~ ^[a-z][a-z0-9-]{0,50}[a-z0-9]$ && "$suffix" != *--* ]] || { echo 'Invalid revision suffix.' >&2; exit 2; }
previous=$(az containerapp revision list --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --query '[?properties.trafficWeight==`100`].name | [0]' --output tsv --only-show-errors)
[[ -n "$previous" && "$previous" != None ]] || { echo 'A single serving revision is required; resolve an existing split rollout first.' >&2; exit 1; }

bash scripts/run-migration.sh "$AZURE_RESOURCE_GROUP" "$MIGRATION_JOB_NAME" "$image"

candidate="${CONTAINER_APP_NAME}--${suffix}"
completed=false
rollback() {
  local status=$?
  trap - EXIT
  if [[ "$completed" != true ]]; then
    echo "Restoring traffic to $previous; database migrations are not reversed." >&2
    az containerapp ingress traffic set --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
      --revision-weight "$previous=100" --only-show-errors --output none || true
    az containerapp revision deactivate --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
      --revision "$candidate" --only-show-errors --output none || true
  fi
  exit "$status"
}
trap rollback EXIT
trap 'exit 130' INT TERM

# Freeze routing before adding a revision, including when bootstrap used latestRevision=true.
az containerapp ingress traffic set --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --revision-weight "$previous=100" --only-show-errors --output none
az containerapp update --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --image "$image" --revision-suffix "$suffix" --only-show-errors --output none

healthy=false
for attempt in $(seq 1 60); do
  health=$(az containerapp revision show --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
    --revision "$candidate" --query properties.healthState --output tsv --only-show-errors)
  if [[ "$health" == Healthy ]]; then healthy=true; break; fi
  sleep 5
done
[[ "$healthy" == true ]] || { echo 'Candidate did not become healthy in five minutes.' >&2; exit 1; }
candidate_host=$(az containerapp revision show --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --revision "$candidate" --query properties.fqdn --output tsv --only-show-errors)
[[ "$candidate_host" =~ ^[a-z0-9.-]+\.azurecontainerapps\.io$ ]] || { echo 'Unexpected candidate hostname.' >&2; exit 1; }
python3 scripts/smoke-api.py --base-url "https://${candidate_host}"

az containerapp ingress traffic set --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --revision-weight "$candidate=100" --only-show-errors --output none
app_host=$(az containerapp show --resource-group "$AZURE_RESOURCE_GROUP" --name "$CONTAINER_APP_NAME" \
  --query properties.configuration.ingress.fqdn --output tsv --only-show-errors)
[[ "$app_host" =~ ^[a-z0-9.-]+\.azurecontainerapps\.io$ ]] || { echo 'Unexpected app hostname.' >&2; exit 1; }
for attempt in $(seq 1 6); do
  python3 scripts/smoke-api.py --base-url "https://${app_host}"
  sleep 5
done
completed=true
mkdir -p artifacts
python3 - "$image" "$previous" "$candidate" "https://${app_host}" <<'PY' > artifacts/deployment.json
import datetime,json,sys
print(json.dumps(dict(image=sys.argv[1], previousRevision=sys.argv[2], revision=sys.argv[3],
    url=sys.argv[4], deployedAt=datetime.datetime.now(datetime.timezone.utc).isoformat()), indent=2))
PY
echo "Deployed $image to https://${app_host}; previous revision: $previous"
