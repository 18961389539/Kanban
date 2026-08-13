# 一键启动 Kanban 三组件：PlcSimulator → Kanban.Collector → MainAPP
#
# 功能：
#   1. 杀掉已开启的旧实例（MainAPP / Collector 的 dotnet 进程、PlcSimulator.exe，
#      并尝试停止 KanbanPlcSimulator / KanbanCollector 服务防止 nssm 复活旧实例）
#   2. 重新构建三个项目（Debug），保证启动的是最新产物
#   3. 按顺序启动并做 Collector 健康检查
#
# 用法（在项目根目录）：
#   .\start_env.ps1                 # 杀旧实例 + 构建 + 启动
#   .\start_env.ps1 -SkipBuild      # 不构建，直接用现有产物启动（启动更快）
#   .\start_env.ps1 -KeepServices   # 不动已安装的 nssm/sc 服务（不尝试停止）
#
# 说明：
#   - 数据源统一约定：不设置 KANBAN_DATA_DIR，三组件都使用默认 %APPDATA%/Kanban。
#   - 若 PlcSimulator 由 nssm 服务（KanbanPlcSimulator）托管，普通用户可能无法停止服务，
#     脚本会杀进程后立即启动新实例；若 5 秒后旧实例被 nssm 复活并抢占 4999 端口，
#     请以管理员身份执行一次：sc.exe stop KanbanPlcSimulator（或卸载该服务）。

param(
    [switch]$SkipBuild,
    [switch]$KeepServices
)

$ErrorActionPreference = "Continue"
$Root = $PSScriptRoot

$SimExe        = Join-Path $Root "PlcSimulator\bin\Debug\net10.0-windows\PlcSimulator.exe"
$CollectorDll  = Join-Path $Root "Kanban.Collector\bin\Debug\net10.0-windows\Kanban.Collector.dll"
$MainAppDll    = Join-Path $Root "MainAPP\bin\Debug\net10.0-windows\MainAPP.dll"
$ServiceSim    = "KanbanPlcSimulator"
$ServiceCol    = "KanbanCollector"

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ───────────────────────── 1/4 停止旧实例 ─────────────────────────
Write-Step "1/4 停止旧实例"

# a) dotnet 托管的 MainAPP / Kanban.Collector（按命令行匹配，避免误杀 IDE / MSBuild 等）
$escapedRoot = [regex]::Escape($Root)
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -and
        $_.CommandLine -match $escapedRoot -and
        $_.CommandLine -match 'MainAPP\.dll|Kanban\.Collector\.dll'
    } |
    ForEach-Object {
        Write-Host "   杀掉 dotnet PID $($_.ProcessId)（$($_.Name)）"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

# b) 独立进程：PlcSimulator.exe、Kanban.Collector.exe（服务模式进程名）
foreach ($procName in @("PlcSimulator", "Kanban.Collector")) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "   杀掉 $($_.ProcessName) PID $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

# c) nssm / sc 服务：尝试停止，防止崩溃自动重启策略（5s）把旧实例拉起来
if (-not $KeepServices) {
    foreach ($svcName in @($ServiceSim, $ServiceCol)) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if ($null -ne $svc -and $svc.Status -ne "Stopped") {
            try {
                Stop-Service -Name $svcName -Force -ErrorAction Stop
                Write-Host "   已停止服务 $svcName"
            } catch {
                Write-Host "   警告：无法停止服务 $svcName（需要管理员权限）。" -ForegroundColor Yellow
                Write-Host "         若 nssm 复活旧实例抢占端口，请管理员执行：sc.exe stop $svcName" -ForegroundColor Yellow
            }
        }
    }
}

Start-Sleep -Seconds 2

# ───────────────────────── 2/4 构建最新产物 ─────────────────────────
Write-Step "2/4 构建最新产物（Debug）"
if (-not $SkipBuild) {
    foreach ($proj in @(
        "PlcSimulator\PlcSimulator.csproj",
        "Kanban.Collector\Kanban.Collector.csproj",
        "MainAPP\MainAPP.csproj")) {
        Write-Host "   dotnet build $proj"
        dotnet build (Join-Path $Root $proj) -c Debug --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            Write-Host "   构建失败：$proj，已中止。" -ForegroundColor Red
            exit 1
        }
    }
} else {
    Write-Host "   -SkipBuild：使用现有产物"
}

# ───────────────────────── 3/4 启动三组件 ─────────────────────────
Write-Step "3/4 启动三组件"

if (-not (Test-Path $SimExe)) { Write-Host "   缺少 $SimExe，请先构建 PlcSimulator。" -ForegroundColor Red; exit 1 }
Start-Process -FilePath $SimExe -WorkingDirectory (Split-Path $SimExe -Parent) -WindowStyle Minimized
Write-Host "   已启动 PlcSimulator（监听 4999）"

if (-not (Test-Path $CollectorDll)) { Write-Host "   缺少 $CollectorDll，请先构建 Kanban.Collector。" -ForegroundColor Red; exit 1 }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
Start-Process -FilePath $dotnet -ArgumentList $CollectorDll -WorkingDirectory (Split-Path $CollectorDll -Parent) -WindowStyle Minimized
Write-Host "   已启动 Kanban.Collector（监听 5129）"

if (-not (Test-Path $MainAppDll)) { Write-Host "   缺少 $MainAppDll，请先构建 MainAPP。" -ForegroundColor Red; exit 1 }
Start-Process -FilePath $dotnet -ArgumentList $MainAppDll -WorkingDirectory (Split-Path $MainAppDll -Parent)
Write-Host "   已启动 MainAPP（大屏窗口）"

# ───────────────────────── 4/4 健康检查 ─────────────────────────
Write-Step "4/4 健康检查（Collector /healthz，最长等待 30s）"
$ok = $false
$content = ""
for ($i = 0; $i -lt 15; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:5129/healthz" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        if ($r.StatusCode -eq 200) { $ok = $true; $content = $r.Content; break }
    } catch { }
    Start-Sleep -Seconds 2
}
if ($ok) {
    Write-Host "   Collector 就绪：/healthz = $content" -ForegroundColor Green
} else {
    Write-Host "   警告：30 秒内 Collector 未就绪，请检查日志。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "完成。进程与日志：" -ForegroundColor Green
Write-Host "   PlcSimulator : 端口 4999（最小化窗口）"
Write-Host "   Kanban.Collector : 端口 5129，日志 %APPDATA%\Kanban\Config\Logs\collector_.log"
Write-Host "   MainAPP      : 大屏窗口，日志 $Root\MainAPP\bin\Debug\net10.0-windows\Logs\"
