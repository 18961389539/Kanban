[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [int]$MinimumLineCoverage = 35,
    # 单向阈值：只允许调低，不允许调高。
    # 上升意味着"为了让门变绿而放宽标准"，会让译文覆盖率持续劣化且无人察觉。
    # 基线（2026-08-31 pt-BR 全量翻译后）：ratio≈2.4% / count=52，剩余为合理回落
    # （专有名词 OK/NG/OEE、数据类型名、ASCII 文件名、纯占位符）。
    [double]$MaxWpfEnglishFallbackRatio = 0.05,
    [int]$MaxWpfEnglishFallbackCount = 60,
    [switch]$SkipVulnerabilityScan
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host "==> localization source and generated artifacts"
    & "$repoRoot\bin-shim\python.cmd" "$PSScriptRoot\generate_localization.py" --check --coverage-report --max-wpf-english-fallback-ratio $MaxWpfEnglishFallbackRatio --max-wpf-english-fallback-count $MaxWpfEnglishFallbackCount
    if ($LASTEXITCODE -ne 0) { throw "localization generation check failed" }

    Write-Host "==> hardcoded Chinese display strings (baseline guard)"
    & "$repoRoot\bin-shim\python.cmd" "$PSScriptRoot\scan_chinese_leaks.py" --check
    if ($LASTEXITCODE -ne 0) { throw "new hardcoded Chinese display strings detected" }

    Write-Host "==> bare StringFormat in XAML"
    & "$repoRoot\bin-shim\python.cmd" "$PSScriptRoot\scan_bare_stringformat.py"
    if ($LASTEXITCODE -ne 0) { throw "bare StringFormat detected" }

    Write-Host "==> architecture gates"
    & "$PSScriptRoot\architecture-gates.ps1"
    if ($LASTEXITCODE -ne 0) { throw "architecture gates failed" }

    Write-Host "==> solution build"
    dotnet build Kanban.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "solution build failed" }

    New-Item -ItemType Directory -Path "artifacts" -Force | Out-Null
    if (-not $SkipVulnerabilityScan) {
        Write-Host "==> dependency vulnerability scan"
        dotnet list Kanban.slnx package --vulnerable --include-transitive --format json --output-version 1 | Tee-Object -FilePath "artifacts\dependency-audit.json"
        if ($LASTEXITCODE -ne 0) { throw "dependency vulnerability scan failed" }
    }

    New-Item -ItemType Directory -Path "artifacts\coverage" -Force | Out-Null
    Write-Host "==> core coverage gate"
    dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj -c $Configuration --no-restore --coverlet --results-directory artifacts\coverage --no-progress
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }

    Write-Host "==> PLC simulator tests"
    dotnet test --project PlcSimulator.Tests\PlcSimulator.Tests.csproj -c $Configuration --no-restore -- --no-progress
    if ($LASTEXITCODE -ne 0) { throw "PLC simulator tests failed" }

    $reports = Get-ChildItem "artifacts\coverage" -Recurse -Filter "coverage.cobertura*.xml"
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
