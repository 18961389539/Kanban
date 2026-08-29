# 一键启动 Kanban 三组件：PlcSimulator → Kanban.Collector → MainAPP
#
# 功能：
#   1. 停止 KanbanPlcSimulator / KanbanCollector 服务并清理同工作区旧实例，防止服务复活旧进程
#   2. 重新构建三个项目（Debug），保证启动的是最新产物
#   3. 按顺序启动并校验 PID、端口和 Collector 业务 readiness
#
# 用法（在项目根目录）：
#   .\start_env.ps1                 # 杀旧实例 + 构建 + 启动（PlcSimulator 默认 --fresh）
#   .\start_env.ps1 -SkipBuild      # 不构建，直接用现有产物启动（启动更快）
#   .\start_env.ps1 -KeepServices   # 保留已安装服务；若服务在运行则拒绝启动调试实例
#   .\start_env.ps1 -RestorePlc     # PlcSimulator 从 PLC 内存恢复（保留状态字 0 等，易卡在离线）
#
# 说明：
#   - 默认对 PlcSimulator 加 --fresh：清零 PLC 并置待机(3)，避免上次断连仿真残留状态字 0 导致一直显示离线。
#   - 需要延续上次 PLC 内存状态时用 -RestorePlc。
#   - 数据源统一约定：不设置 KANBAN_DATA_DIR，三组件都使用默认 %APPDATA%/Kanban。
#   - 服务化实例运行时不能与本地 Debug 实例共用 4999/5129 端口；请先停止服务，
#     或使用 -KeepServices 仅查看服务状态而不启动本地实例。

param(
    [switch]$SkipBuild,
    [switch]$KeepServices,
    [switch]$RestorePlc
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

$SimExe        = Join-Path $Root "PlcSimulator\bin\Debug\net10.0-windows\PlcSimulator.exe"
$CollectorDll  = Join-Path $Root "Kanban.Collector\bin\Debug\net10.0-windows\Kanban.Collector.dll"
$MainAppDll    = Join-Path $Root "MainAPP\bin\Debug\net10.0-windows\MainAPP.dll"
$ServiceSim    = "KanbanPlcSimulator"
$ServiceCol    = "KanbanCollector"
$HmacEnvName   = "KANBAN_HMAC_KEY"
$SimPort       = 4999
$CollectorPort = 5129
$startedProcesses = @()

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Resolve-HmacKey {
    $candidate = $env:KANBAN_HMAC_KEY
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable($HmacEnvName, [EnvironmentVariableTarget]::Machine)
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        Remove-Item "Env:\$HmacEnvName" -ErrorAction SilentlyContinue
        Write-Host "未配置 $HmacEnvName；MainAPP 在没有 license.dat 时将使用 30 天试用。" -ForegroundColor Yellow
        return $null
    }

    $candidate = $candidate.Trim()
    try { $bytes = [Convert]::FromBase64String($candidate) }
    catch { throw "$HmacEnvName 不是合法的 Base64 编码。" }
    if ($bytes.Length -ne 32) {
        throw "$HmacEnvName 长度非法：必须是 32 字节随机密钥的 Base64 形式。"
    }
    $env:KANBAN_HMAC_KEY = $candidate
    return $candidate
}

function Get-ListeningProcessIds([int]$port) {
    @(
        Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
    )
}

function Get-PortOwnerText([int]$port) {
    $owners = @(Get-ListeningProcessIds $port)
    if ($owners.Count -eq 0) { return "无监听进程" }

    $details = foreach ($owner in $owners) {
        $process = Get-Process -Id $owner -ErrorAction SilentlyContinue
        if ($null -ne $process) { "$($process.ProcessName) PID $owner" }
        else { "PID $owner" }
    }
    return ($details -join ", ")
}

function Stop-ManagedService([string]$serviceName) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq "Stopped") { return }

    if ($KeepServices) {
        throw "服务 $serviceName 正在运行；-KeepServices 不会停止服务，因此不能启动本地 Debug 实例。"
    }

    Write-Host "   请求停止服务 $serviceName"
    try {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
    }
    catch {
        throw "无法停止服务 $serviceName（可能需要管理员权限）：$($_.Exception.Message)"
    }

    $deadline = (Get-Date).AddSeconds(15)
    do {
        $service.Refresh()
        if ($service.Status -eq "Stopped") { return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "服务 $serviceName 未能在 15 秒内停止（当前状态：$($service.Status)）。"
}

function Test-ProcessAlive([System.Diagnostics.Process]$process) {
    if ($null -eq $process) { return $false }
    try {
        $process.Refresh()
        return -not $process.HasExited
    }
    catch {
        return $false
    }
}

function Stop-StartedProcesses {
    foreach ($process in @($startedProcesses)) {
        if (Test-ProcessAlive $process) {
            Write-Host "   回收本次启动的进程 PID $($process.Id)"
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Wait-ForComponents(
    [System.Diagnostics.Process]$simulator,
    [System.Diagnostics.Process]$collector,
    [System.Diagnostics.Process]$mainApp) {
    $lastReadyError = "尚未响应"

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (-not (Test-ProcessAlive $simulator)) {
            throw "PlcSimulator 已退出，无法确认端口 $SimPort。"
        }
        if (-not (Test-ProcessAlive $collector)) {
            throw "Kanban.Collector 已退出，无法确认端口 $CollectorPort。"
        }
        if (-not (Test-ProcessAlive $mainApp)) {
            throw "MainAPP 已退出，请检查授权、配置和日志。"
        }

        $simOwners = @(Get-ListeningProcessIds $SimPort)
        if ($simOwners.Count -gt 0 -and -not ($simOwners -contains $simulator.Id)) {
            throw "端口 $SimPort 被其他进程占用：$(Get-PortOwnerText $SimPort)"
        }

        $collectorOwners = @(Get-ListeningProcessIds $CollectorPort)
        if ($collectorOwners.Count -gt 0 -and -not ($collectorOwners -contains $collector.Id)) {
            throw "端口 $CollectorPort 被其他进程占用：$(Get-PortOwnerText $CollectorPort)"
        }

        $simReady = $simOwners -contains $simulator.Id
        $collectorReady = $false
        if ($collectorOwners -contains $collector.Id) {
            try {
                $response = Invoke-WebRequest -Uri "http://127.0.0.1:$CollectorPort/health/ready" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
                if ($response.StatusCode -eq 200) {
                    $collectorReady = $true
                }
                else {
                    $lastReadyError = "HTTP $($response.StatusCode)"
                }
            }
            catch {
                $lastReadyError = $_.Exception.Message
            }
        }

        if ($simReady -and $collectorReady) { return }
        Start-Sleep -Seconds 1
    }

    throw "组件未在 30 秒内就绪：PlcSimulator=$simReady，Collector readiness=$lastReadyError。"
}

# 启动前先解析授权密钥。没有密钥时保留 MainAPP 的 30 天试用路径，
# 但提供了非法密钥仍立即失败，避免使用错误密钥启动正式授权环境。
$hmacKey = Resolve-HmacKey
if ([string]::IsNullOrWhiteSpace($hmacKey)) {
    Write-Host "   未配置 $HmacEnvName，按试用模式启动 MainAPP" -ForegroundColor Yellow
}
else {
    Write-Host "   $HmacEnvName 已配置（不显示密钥内容）" -ForegroundColor Green
}

# ───────────────────────── 1/4 停止旧实例 ─────────────────────────
Write-Step "1/4 停止旧实例"

# 先停托管服务，再处理同目录进程；否则服务可能在杀进程后自动拉起旧实例。
foreach ($svcName in @($ServiceSim, $ServiceCol)) {
    Stop-ManagedService $svcName
}

# a) 按完整命令行匹配同工作区目标进程，避免误杀 IDE / MSBuild 等
$escapedRoot = [regex]::Escape($Root)
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -and
        $_.CommandLine -match $escapedRoot -and
        $_.CommandLine -match 'PlcSimulator\.(dll|exe)|Kanban\.Collector\.(dll|exe)|MainAPP\.(dll|exe)'
    } |
    ForEach-Object {
        Write-Host "   杀掉旧进程 PID $($_.ProcessId)（$($_.Name)）"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

$portDeadline = (Get-Date).AddSeconds(15)
do {
    $busyPorts = @(
        foreach ($port in @($SimPort, $CollectorPort)) {
            if (@(Get-ListeningProcessIds $port).Count -gt 0) { $port }
        }
    )
    if ($busyPorts.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $portDeadline)
if ($busyPorts.Count -gt 0) {
    $busyPortDetails = @($busyPorts | ForEach-Object { "$_ ($(Get-PortOwnerText $_))" })
    throw "旧实例清理后端口仍被占用：$($busyPortDetails -join ', ')"
}

# ───────────────────────── 2/4 构建最新产物 ─────────────────────────
Write-Step "2/4 构建最新产物（Debug）"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "找不到 dotnet：$dotnet" }

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

if (-not (Test-Path $SimExe)) { throw "缺少 $SimExe，请先构建 PlcSimulator。" }
if (-not (Test-Path $CollectorDll)) { throw "缺少 $CollectorDll，请先构建 Kanban.Collector。" }
if (-not (Test-Path $MainAppDll)) { throw "缺少 $MainAppDll，请先构建 MainAPP。" }

$simProcess = $null
$collectorProcess = $null
$mainAppProcess = $null
try {
    $simArgs = @()
    if (-not $RestorePlc) { $simArgs += "--fresh" }
    $simProcess = Start-Process -FilePath $SimExe -ArgumentList $simArgs `
        -WorkingDirectory (Split-Path $SimExe -Parent) -WindowStyle Minimized -PassThru -ErrorAction Stop
    $startedProcesses += $simProcess
    $simMode = if ($RestorePlc) { "恢复 PLC 状态" } else { "全新初始化(--fresh)" }
    Write-Host "   已启动 PlcSimulator PID $($simProcess.Id)（监听 $SimPort，$simMode）"

    $collectorProcess = Start-Process -FilePath $dotnet -ArgumentList $CollectorDll -WorkingDirectory (Split-Path $CollectorDll -Parent) -WindowStyle Minimized -PassThru -ErrorAction Stop
    $startedProcesses += $collectorProcess
    Write-Host "   已启动 Kanban.Collector PID $($collectorProcess.Id)（监听 $CollectorPort）"

    $mainAppProcess = Start-Process -FilePath $dotnet -ArgumentList $MainAppDll -WorkingDirectory (Split-Path $MainAppDll -Parent) -PassThru -ErrorAction Stop
    $startedProcesses += $mainAppProcess
    Write-Host "   已启动 MainAPP PID $($mainAppProcess.Id)（大屏窗口）"

    # ───────────────────────── 4/4 健康检查 ─────────────────────────
    Write-Step "4/4 进程与业务就绪检查（最长等待 30s）"
    Wait-ForComponents $simProcess $collectorProcess $mainAppProcess
    Write-Host "   PlcSimulator 进程与端口校验通过：PID $($simProcess.Id) / $SimPort" -ForegroundColor Green
    Write-Host "   Collector 进程与 /health/ready 校验通过：PID $($collectorProcess.Id) / $CollectorPort" -ForegroundColor Green
    Write-Host "   MainAPP 进程存活校验通过：PID $($mainAppProcess.Id)" -ForegroundColor Green
}
catch {
    Write-Host "   启动失败：$($_.Exception.Message)" -ForegroundColor Red
    Stop-StartedProcesses
    exit 1
}

Write-Host ""
Write-Host "完成。进程与日志：" -ForegroundColor Green
Write-Host "   PlcSimulator : 端口 $SimPort（最小化窗口）"
Write-Host "   Kanban.Collector : 端口 $CollectorPort，日志 %APPDATA%\Kanban\Config\Logs\collector_.log"
Write-Host "   MainAPP      : 大屏窗口，日志 $Root\MainAPP\bin\Debug\net10.0-windows\Logs\"
