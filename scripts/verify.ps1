[CmdletBinding()]
param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
$solution = if ($Desktop) { 'Teammy.sln' } else { 'Teammy.Server.slnf' }
New-Item -ItemType Directory -Force 'artifacts/test-results' | Out-Null
Invoke-Dotnet restore $solution --locked-mode
Invoke-Dotnet format $solution --verify-no-changes --no-restore
Invoke-Dotnet build $solution --configuration Release --no-restore
Invoke-Dotnet test Teammy.Server.slnf --configuration Release --no-build --logger trx --collect 'XPlat Code Coverage' --results-directory artifacts/test-results
python scripts/check-coverage.py artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'Coverage gate failed' }
& dotnet list $solution package --vulnerable --include-transitive --format json | Set-Content -Encoding utf8 artifacts/nuget-audit.json
if ($LASTEXITCODE -ne 0) { throw 'NuGet audit failed' }
python scripts/check-vulnerabilities.py artifacts/nuget-audit.json
if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerabilities found' }
