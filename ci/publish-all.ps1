# Publish all deployable Kanban programs into ONE output directory.
#
#   Collector  (service exe) -> <Out>/Collector   (+ wwwroot/WASM served by Collector)
#   MainAPP    (WPF exe)      -> <Out>/MainAPP
#   PlcSim     (simulator)    -> <Out>/PlcSimulator
#   Web (WASM)                -> copied into <Out>/Collector/wwwroot
#
# Single-port story: Collector serves the WASM from its own wwwroot, so the
# browser opens http://<host>:5129/ with no extra host/CORS/firewall rule.
#
# Usage:
#   .\ci\publish-all.ps1                              # Release, framework-dependent -> ./publish
#   .\ci\publish-all.ps1 -Configuration Debug
#   .\ci\publish-all.ps1 -SelfContained -Runtime win-x64   # bundle .NET runtime, no install needed
#   .\ci\publish-all.ps1 -OutputDir D:\KanbanDeploy -SkipWeb -SkipSim
#
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 parses ps1 as ANSI).

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$SkipWeb,
    [switch]$SkipSim
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "publish" }

$collOut = Join-Path $OutputDir "Collector"
$mainOut = Join-Path $OutputDir "MainAPP"
$simOut  = Join-Path $OutputDir "PlcSimulator"
$webTemp = Join-Path $OutputDir "_wasm_tmp"

function Invoke-Publish {
    param([string]$Project, [string]$Out)
    Write-Host "==> publish $Project -> $Out"
    $args = @("publish", $Project, "-c", $Configuration, "-o", $Out, "--nologo", "-v", "minimal")
    if ($SelfContained) { $args += @("-r", $Runtime, "--self-contained") }
    else { $args += "--no-self-contained" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $Project (exit $LASTEXITCODE)" }

    # 发布剥离调试符号（授权设计文档 checklist：pdb 不得随发布包外发；审查修复 2026-08-13）
    $pdbs = Get-ChildItem -Path $Out -Recurse -Filter *.pdb -ErrorAction SilentlyContinue
    if ($pdbs) {
        $pdbs | Remove-Item -Force
        Write-Host "    stripped $($pdbs.Count) PDB(s) from $Out"
    }
}

try {
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    # 1) Collector (service + WASM host)
    Invoke-Publish (Join-Path $repoRoot "Kanban.Collector\Kanban.Collector.csproj") $collOut

    # 2) MainAPP (WPF management/display client)
    Invoke-Publish (Join-Path $repoRoot "MainAPP\MainAPP.csproj") $mainOut

    # 3) PlcSimulator (PLC emulator on 127.0.0.1:4999; deploy only when testing/demoing)
    if (-not $SkipSim) {
        Invoke-Publish (Join-Path $repoRoot "PlcSimulator\PlcSimulator.csproj") $simOut
        # Copy runtime resources (e.g. sim_scenarios.json, baselines) from source root
        # into the simulator output so the deployed binary runs identically to dev.
        $simSrc = Join-Path $repoRoot "PlcSimulator"
        Get-ChildItem -Path $simSrc -File -Include *.json,*.txt,*.csv,*.config -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Copy-Item -Destination $simOut -Force
        $simExe = Join-Path $simOut "PlcSimulator.exe"
        if (-not (Test-Path $simExe)) { throw "verify failed: PlcSimulator.exe missing in $simOut" }
        Write-Host "    PlcSim OK: $simExe"
    }
    else {
        Write-Host "==> -SkipSim set: PlcSimulator excluded."
    }

    # 4) Web (WASM) -> into Collector/wwwroot
    if (-not $SkipWeb) {
        Write-Host "==> publish Kanban.Web -> $webTemp"
        & dotnet publish (Join-Path $repoRoot "Kanban.Web\Kanban.Web.csproj") -c $Configuration -o $webTemp --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "publish failed: Kanban.Web (exit $LASTEXITCODE)" }

        # WASM 输出同样剥离调试符号（审查修复 2026-08-13）
        Get-ChildItem -Path $webTemp -Recurse -Filter *.pdb -ErrorAction SilentlyContinue |
            Remove-Item -Force

        $srcWww = Join-Path $webTemp "wwwroot"
        $dstWww = Join-Path $collOut "wwwroot"
        if (-not (Test-Path $srcWww)) { throw "WASM publish missing wwwroot: $srcWww" }

        if (Test-Path $dstWww) { Remove-Item $dstWww -Recurse -Force }
        # Copy the wwwroot container itself (not its children via "*"): the wildcard
        # expands to many items, and PowerShell treats the first file as a leaf, then
        # fails on sub-directories with CopyContainerItemToLeafError. Copying the
        # container creates <collOut>/wwwroot as a directory correctly.
        Copy-Item -Path $srcWww -Destination $dstWww -Recurse -Force

        $blazorJs = Get-ChildItem "$dstWww\_framework\blazor.*.js" -ErrorAction SilentlyContinue
        $icudt    = Get-ChildItem "$dstWww\_framework\icudt_*.dat" -ErrorAction SilentlyContinue
        if (-not $blazorJs) { throw "verify failed: _framework/blazor.*.js missing (WASM cannot start)" }
        if (-not $icudt)    { throw "verify failed: _framework/icudt_*.dat missing (Collector must map .dat MIME)" }
        Write-Host "    WASM assets OK: $($blazorJs.Name) / $($icudt.Name)"
    }
    else {
        Write-Host "==> -SkipWeb set: WASM not included; Collector will not serve the big-screen."
    }

    # 4) ship the service installer next to the output for convenience
    $installer = Join-Path $repoRoot "ci\install-collector-service.ps1"
    if (Test-Path $installer) { Copy-Item $installer $OutputDir -Force }

    Write-Host ""
    Write-Host "==> publish complete: $OutputDir"
    Write-Host "    Collector : $collOut\Kanban.Collector.exe"
    if (-not $SkipWeb) { Write-Host "    WASM     : $collOut\wwwroot  (open http://<host>:5129/)" }
    Write-Host "    MainAPP   : $mainOut\MainAPP.exe"
    if (-not $SkipSim) { Write-Host "    PlcSim    : $simOut\PlcSimulator.exe  (host 4999)" }
    Write-Host ""
    Write-Host "    Install service: .\install-collector-service.ps1 -Action Install -DataRoot <shared-data-dir>"
}
finally {
    if (Test-Path $webTemp) { Remove-Item $webTemp -Recurse -Force }
}
