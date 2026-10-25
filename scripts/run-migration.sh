#!/usr/bin/env bash
# Uses the migration job's identity and owner connection, never the API's runtime role.
set -euo pipefail
if [[ $# -ne 3 ]]; then
  echo 'Usage: scripts/run-migration.sh RESOURCE_GROUP JOB_NAME IMAGE@sha256:DIGEST' >&2
  exit 2
fi
resource_group=$1
job_name=$2
image=$3
[[ "$image" =~ ^[a-z0-9.-]+/[a-z0-9/_.-]+@sha256:[a-f0-9]{64}$ ]] || { echo 'An immutable image digest is required.' >&2; exit 2; }
az containerapp job update --resource-group "$resource_group" --name "$job_name" \
  --image "$image" --only-show-errors --output none
execution=$(az containerapp job start --resource-group "$resource_group" --name "$job_name" \
  --query name --output tsv --only-show-errors)
[[ "$execution" =~ ^[a-z0-9-]+$ ]] || { echo 'Azure did not return a valid execution name.' >&2; exit 1; }
for attempt in $(seq 1 72); do
  status=$(az containerapp job execution show --resource-group "$resource_group" --name "$job_name" \
    --job-execution-name "$execution" --query properties.status --output tsv --only-show-errors)
  case "$status" in
    Succeeded) echo "Migration execution $execution succeeded."; exit 0 ;;
    Failed|Stopped) echo "Migration execution $execution ended: $status" >&2; exit 1 ;;
  esac
  sleep 5
done
echo "Migration execution $execution did not finish within six minutes; no API release was made." >&2
exit 1
