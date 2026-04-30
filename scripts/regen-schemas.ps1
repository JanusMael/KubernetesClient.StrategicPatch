<#
.SYNOPSIS
  Regenerates src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json from
  one or more OpenAPI v3 input documents.

.EXAMPLE
  scripts/regen-schemas.ps1 ../kubernetes/api/openapi-spec/v3/api__v1_openapi.json `
                            ../kubernetes/api/openapi-spec/v3/apis__apps__v1_openapi.json
#>
param(
  [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
  [string[]] $OpenApiInputs
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repoRoot 'src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json'

& dotnet run `
  --project (Join-Path $repoRoot 'src/KubernetesClient.StrategicPatch.SchemaTool') `
  --configuration Release `
  -- $out @OpenApiInputs

if ($LASTEXITCODE -ne 0) {
  throw "SchemaTool exited with $LASTEXITCODE"
}
Write-Host "Wrote $out"
