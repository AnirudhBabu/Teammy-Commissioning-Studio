#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
mkdir -p artifacts/test-results
dotnet restore Teammy.Server.slnf --locked-mode
dotnet format Teammy.Server.slnf --verify-no-changes --no-restore
dotnet build Teammy.Server.slnf --configuration Release --no-restore
dotnet test Teammy.Server.slnf --configuration Release --no-build \
  --logger 'trx' --collect 'XPlat Code Coverage' --results-directory artifacts/test-results
python3 scripts/check-coverage.py artifacts/test-results
dotnet list Teammy.Server.slnf package --vulnerable --include-transitive --format json > artifacts/nuget-audit.json
python3 scripts/check-vulnerabilities.py artifacts/nuget-audit.json
