[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [int]$MinimumLineCoverage = 35,
    [switch]$SkipVulnerabilityScan
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (-not $SkipVulnerabilityScan) {
        Write-Host "==> dependency vulnerability scan"
        dotnet list Kanban.slnx package --vulnerable --include-transitive --format json --output-version 1 | Tee-Object -FilePath "artifacts\dependency-audit.json"
        if ($LASTEXITCODE -ne 0) { throw "dependency vulnerability scan failed" }
    }

    New-Item -ItemType Directory -Path "artifacts\coverage" -Force | Out-Null
    Write-Host "==> core coverage gate"
    dotnet test MainAPP.Tests\MainAPP.Tests.csproj -c $Configuration --collect:"XPlat Code Coverage" --results-directory artifacts\coverage --no-restore -- --no-progress
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }

    $reports = Get-ChildItem "artifacts\coverage" -Recurse -Filter "coverage.cobertura.xml"
    if (-not $reports) { throw "coverage report was not produced" }
    [xml]$report = Get-Content $reports[0].FullName
    $lineRate = [double]$report.coverage.'line-rate'
    $percent = [math]::Round($lineRate * 100, 2)
    Write-Host "line coverage: $percent%"
    if ($percent -lt $MinimumLineCoverage) {
        throw "coverage $percent% is below required $MinimumLineCoverage%"
    }
}
finally {
    Pop-Location
}
