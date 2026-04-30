#Requires -Version 7
param (
    [Parameter(Mandatory=$false)]
    [string]$PackageDirectory = "./packages"
)

$ErrorActionPreference = 'Stop'

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Read-Required {
    param(
        [string]$Prompt,
        [string]$Default = $null,
        [string]$Pattern = $null,
        [string]$PatternHint = $null
    )
    $displayPrompt = $Default ? "$Prompt [$Default]" : $Prompt
    while ($true) {
        $value = (Read-Host $displayPrompt).Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            if ($Default) { return $Default }
            Write-Host "  Required — please enter a value." -ForegroundColor Red
            continue
        }
        if ($Pattern -and $value -notmatch $Pattern) {
            Write-Host "  $PatternHint" -ForegroundColor Red
            continue
        }
        return $value
    }
}

# ─── Prompts ──────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "=== Build → Pack ===" -ForegroundColor Cyan
Write-Host ""

$now     = Get-Date
$quarter = [Math]::Ceiling($now.Month / 3)
$mmdd    = [int]$now.ToString('MMdd')
$calVer  = "$($now.Year).$quarter.$mmdd"

$Version = Read-Required `
    -Prompt "NuGet version" `
    -Default $calVer `
    -Pattern '^\d+\.\d+\.\d+(-[A-Za-z0-9]+(\.[A-Za-z0-9]+)*)?$' `
    -PatternHint "Must be a valid version: major.minor.patch with an optional pre-release label (e.g. 2026.2.429-beta.1)."

$CsprojPath = "src/KubernetesClient.StrategicPatch/KubernetesClient.StrategicPatch.csproj"

# ─── Build and Pack ───────────────────────────────────────────────────────────

foreach ($config in @("Debug", "Release")) {
    $configPackageDir = Join-Path $PackageDirectory $config

    if (Test-Path $configPackageDir) {
        Remove-Item -Path (Join-Path $configPackageDir "*.nupkg") -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Path $configPackageDir | Out-Null
    }

    Write-Host ""
    Write-Host "[$config] Building v$Version..." -ForegroundColor Cyan
    dotnet build --configuration $config --nologo /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build failed ($config)."; exit 1 }

    Write-Host "[$config] Packing v$Version..." -ForegroundColor Cyan
    dotnet pack $CsprojPath --configuration $config --no-build --nologo `
        /p:Version=$Version `
        --output $configPackageDir
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet pack failed ($config)."; exit 1 }
}

# ─── Summary ──────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Done. Packages:" -ForegroundColor Green
Get-ChildItem -Path $PackageDirectory -Filter "*.nupkg" -Recurse |
    ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Green }
