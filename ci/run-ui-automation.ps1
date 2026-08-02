# CI/本地运行的 UI 自动化测试入口脚本。
#
# 用法（PowerShell）：
#   .\ci\run-ui-automation.ps1                  # 仅跑 FlaUI 测试（WinAppDriver 服务未启动时自动跳过 WAD 用例）
#   .\ci\run-ui-automation.ps1 -WithWinAppDriver # 自动安装/启动 WinAppDriver，启用 Appium 冒烟测试后跑全套
#
# 退出码：0 = 全部通过；非 0 = 构建失败或有测试失败。
#
# ⚠️ 并行执行注意（重要）：
#   MainAPP.Tests / MainAPP.E2E / MainAPP.UIAutomation 三个测试项目都引用
#   MainAPP.csproj，共享同一份 obj\Debug\net10.0-windows 编译缓存。若并行执行
#   （同时开多个终端 / CI 并行任务跑 dotnet build|test），会互相删除/锁定 obj
#   缓存，导致构建失败：
#     - System.UnauthorizedAccessException: Access to '...MainAPP_MarkupCompile.cache' is denied.
#     - MC1000: ... because it is being used by another process.
#   正确做法：① 各测试项目串行执行；或 ② 先统一 `dotnet build`，再各自
#   `dotnet test --no-build`（本脚本已采用 --no-build）。
#   本脚本在构建前会检测是否有其它 dotnet 进程在构建/测试本项目，发现则直接
#   退出（exit 2），强制串行，避免静默的诡异构建失败。

[CmdletBinding()]
param(
    [switch]$WithWinAppDriver,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$slnRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$csproj = Join-Path $slnRoot "MainAPP.UIAutomation\MainAPP.UIAutomation.csproj"
$wadDefaultPath = "C:\Program Files\Windows Application Driver\WinAppDriver.exe"
$wadPort = 4723
$wadProcess = $null

function Test-PortListening([int]$Port) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $iar = $tcp.BeginConnect("127.0.0.1", $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(1))
        if ($ok -and $tcp.Connected) { $tcp.Close(); return $true }
        $tcp.Close()
        return $false
    } catch { return $false }
}

function Test-WinAppDriverInstalled {
    return (Test-Path $wadDefaultPath) -or ($null -ne (Get-Command WinAppDriver -ErrorAction SilentlyContinue))
}

function Start-WinAppDriver {
    if (Test-PortListening $wadPort) {
        Write-Host "[WAD] 端口 $wadPort 已被占用，假定 WinAppDriver 已在运行。" -ForegroundColor Yellow
        return $null
    }
    if (-not (Test-WinAppDriverInstalled)) {
        Write-Host "[WAD] 未安装 WinAppDriver，跳过启动（Appium 测试将自动 Skip）。" -ForegroundColor Yellow
        Write-Host "[WAD] 安装方法：choco install winappdriver -y 或访问 https://github.com/microsoft/WinAppDriver/releases" -ForegroundColor Yellow
        return $null
    }
    $exe = if (Test-Path $wadDefaultPath) { $wadDefaultPath } else { "WinAppDriver" }
    Write-Host "[WAD] 启动 $exe $wadPort ..." -ForegroundColor Cyan
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "127.0.0.1 $wadPort"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = Split-Path $exe -ErrorAction SilentlyContinue
    $p = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 300
        if (Test-PortListening $wadPort) { break }
    }
    if (-not (Test-PortListening $wadPort)) {
        Write-Host "[WAD] WinAppDriver 启动失败（端口未监听）。" -ForegroundColor Red
        return $null
    }
    Write-Host "[WAD] WinAppDriver 已监听 127.0.0.1:$wadPort (PID $($p.Id))" -ForegroundColor Green
    return $p
}

function Stop-WinAppDriver($p) {
    if ($null -eq $p) { return }
    try {
        if (-not $p.HasExited) {
            $p.Kill()
            $p.WaitForExit(3000) | Out-Null
        }
        $p.Dispose()
        Write-Host "[WAD] WinAppDriver 已停止 (PID $($p.Id))" -ForegroundColor Cyan
    } catch { Write-Host "[WAD] 停止失败：$_" -ForegroundColor Yellow }
}

# 并行构建防护：检测是否已有其它 dotnet 进程在构建/测试本项目。
# 三个测试项目共享 MainAPP 的 obj 缓存，并行会互相锁死导致诡异构建失败。
function Assert-NoConcurrentTestBuild {
    try {
        $others = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine } |
            Where-Object { $_.CommandLine -match 'MainAPP\.(Tests|E2E|UIAutomation|Benchmarks)\.csproj|MainAPP\\MainAPP\.csproj' }
        if ($others) {
            Write-Host "[CI] 检测到其它 dotnet 进程正在构建/测试本项目，可能争抢 MainAPP 的 obj 缓存导致构建失败：" -ForegroundColor Red
            $others | ForEach-Object { Write-Host "    PID $($_.ProcessId): $($_.CommandLine)" -ForegroundColor Red }
            Write-Host "[CI] 请串行执行各测试项目，或统一先 'dotnet build' 再各自 'dotnet test --no-build'。" -ForegroundColor Yellow
            exit 2
        }
    } catch {
        # 获取进程命令行失败（如无 WMI 权限）时不阻断，仅跳过并发检测
        Write-Host "[CI] 跳过并发检测：$_" -ForegroundColor DarkGray
    }
}
Assert-NoConcurrentTestBuild

Write-Host "==== 构建 MainAPP.UIAutomation ($Configuration) ====" -ForegroundColor Cyan
dotnet build $csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "构建失败" -ForegroundColor Red; exit 1 }

if ($WithWinAppDriver) {
    $wadProcess = Start-WinAppDriver
    if ($null -eq $wadProcess) {
        Write-Host "[WAD] 未启用 WinAppDriver；Appium 用例将 Skip。" -ForegroundColor Yellow
    } else {
        # 设置环境变量：通知 WinAppDriverSmokeTests 真正运行 Appium 测试
        $env:KANBAN_RUN_WAD_TESTS = "1"
        Write-Host "[WAD] 已设置 KANBAN_RUN_WAD_TESTS=1，Appium 冒烟测试将运行" -ForegroundColor Green
    }
} else {
    Write-Host "[WAD] 跳过 WinAppDriver 启动（使用 -WithWinAppDriver 启用）" -ForegroundColor Yellow
}

Write-Host "==== 运行 UI 自动化测试 ====" -ForegroundColor Cyan
try {
    dotnet test --project $csproj -c $Configuration --no-build
    $exitCode = $LASTEXITCODE
} finally {
    Stop-WinAppDriver $wadProcess
    Remove-Item Env:KANBAN_RUN_WAD_TESTS -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) {
    Write-Host "==== 测试全部通过 ====" -ForegroundColor Green
} else {
    Write-Host "==== 测试存在失败，exit code = $exitCode ====" -ForegroundColor Red
}
exit $exitCode
