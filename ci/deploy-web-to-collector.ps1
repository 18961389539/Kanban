# Single-port deploy: publish Kanban.Web (WASM) and copy to Collector output wwwroot/
# Collector serves this directory as static files (ContentRoot fixed to exe dir),
# browser opens the kanban at http://<host-ip>:5129/ (same-origin, no CORS needed).
#
# Usage:
#   .\ci\deploy-web-to-collector.ps1                          # Debug (default)
#   .\ci\deploy-web-to-collector.ps1 -Configuration Release
#   .\ci\deploy-web-to-collector.ps1 -SkipBuild               # copy only, skip publish
#
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 parses ps1 as ANSI).

param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$webProject = Join-Path $repoRoot "Kanban.Web\Kanban.Web.csproj"
$publishDir = Join-Path $repoRoot "Kanban.Web\bin\$Configuration\net10.0-browser\publish\wwwroot"
$collectorOut = Join-Path $repoRoot "Kanban.Collector\bin\$Configuration\net10.0-windows"
$targetWwwRoot = Join-Path $collectorOut "wwwroot"

if (-not $SkipBuild) {
    Write-Host "==> publish Kanban.Web ($Configuration)"
    dotnet publish $webProject -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path $publishDir)) { throw "publish output missing: $publishDir (run publish first or check -Configuration)" }
if (-not (Test-Path $collectorOut)) { throw "Collector output missing: $collectorOut (build Kanban.Collector first)" }

Write-Host "==> copy WASM -> $targetWwwRoot"
if (Test-Path $targetWwwRoot) { Remove-Item $targetWwwRoot -Recurse -Force }
New-Item -ItemType Directory -Path $targetWwwRoot -Force | Out-Null
Copy-Item -Path "$publishDir\*" -Destination $targetWwwRoot -Recurse -Force

# ---- verify critical assets ----
$blazorJs = Get-ChildItem "$targetWwwRoot\_framework\blazor.*.js" -ErrorAction SilentlyContinue
$icudt = Get-ChildItem "$targetWwwRoot\_framework\icudt_*.dat" -ErrorAction SilentlyContinue
if (-not $blazorJs) { throw "verify failed: _framework/blazor.*.js missing (WASM cannot start)" }
if (-not $icudt) { throw "verify failed: _framework/icudt_*.dat missing (Collector must map .dat MIME)" }

Write-Host "==> deployed: $targetWwwRoot"
Write-Host "    entry: http://<host-ip>:5129/  (Collector on 0.0.0.0:5129)"
Write-Host "    assets: $($blazorJs.Name) / $($icudt.Name)"

# ---- version stamp (upgrade traceability) ----
$collectorCsproj = Join-Path $repoRoot "Kanban.Collector\Kanban.Collector.csproj"
$versionMatch = Select-String -Path $collectorCsproj -Pattern "<Version>([^<]+)</Version>"
if ($versionMatch) {
    Write-Host "    collector version: $($versionMatch.Matches[0].Groups[1].Value)  (GetServerVersionAsync / /metrics)"
}
