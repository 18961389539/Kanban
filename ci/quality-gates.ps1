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
    New-Item -ItemType Directory -Path "artifacts" -Force | Out-Null
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
    $covered = 0L
    $valid = 0L
    foreach ($reportFile in $reports) {
        [xml]$report = Get-Content $reportFile.FullName
        $covered += [long]$report.coverage.'lines-covered'
        $valid += [long]$report.coverage.'lines-valid'
    }
    if ($valid -le 0) { throw "coverage reports contain no valid lines" }
    $percent = [math]::Round(($covered / [double]$valid) * 100, 2)
    Write-Host "line coverage: $percent%"
    if ($percent -lt $MinimumLineCoverage) {
        throw "coverage $percent% is below required $MinimumLineCoverage%"
    }
}
finally {
    Pop-Location
}
