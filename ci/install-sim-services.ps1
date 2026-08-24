# 一键安装 PlcSimulator + Kanban.Collector 为 Windows 服务（开机自启 + 崩溃自动重启）
#
# 用法（必须以管理员身份运行）：
#   右键"以管理员身份运行" PowerShell，然后：
#   .\ci\install-sim-services.ps1 -HmacKey "<与LicenseIssuer相同的32字节Base64密钥>"
#
# 安装内容：
#   1. KanbanCollector 服务：sc.exe 注册 Kanban.Collector.exe（端口 5129，数据目录 run-demo/data）
#   2. KanbanPlcSimulator 服务：NSSM 包装 PlcSimulator.exe（端口 4999，与 settings.json 的 PlcConfig.Port 一致）
#   3. 崩溃自动重启：Collector 5s/10s/30s 三次；PlcSimulator 5s/10s/30s 三次
#
# 卸载：
#   .\ci\install-sim-services.ps1 -Action Uninstall

param(
    [ValidateSet("Install", "Uninstall", "Status")]
    [string]$Action = "Install",
    [string]$HmacKey = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$DataRoot = Join-Path $RepoRoot "run-demo\data"
$CollectorExe = Join-Path $RepoRoot "Kanban.Collector\bin\Debug\net10.0-windows\Kanban.Collector.exe"
$SimExe = Join-Path $RepoRoot "PlcSimulator\bin\Debug\net10.0-windows\PlcSimulator.exe"
$NssmExe = Join-Path $RepoRoot "ci\nssm.exe"
$ServiceCollector = "KanbanCollector"
$ServiceSim = "KanbanPlcSimulator"
$HmacEnvName = "KANBAN_HMAC_KEY"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Resolve-HmacKey([string]$provided) {
    $candidate = $provided
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:KANBAN_HMAC_KEY
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable($HmacEnvName, [EnvironmentVariableTarget]::Machine)
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw "未配置 $HmacEnvName。请先运行 .\ci\configure-hmac-key.ps1 -HmacKey <与LicenseIssuer相同的32字节Base64密钥>；仅开发/演示环境可使用 -Generate。"
    }

    $candidate = $candidate.Trim()
    try { $bytes = [Convert]::FromBase64String($candidate) }
    catch { throw "$HmacEnvName 不是合法的 Base64 编码。" }
    if ($bytes.Length -ne 32) {
        throw "$HmacEnvName 长度非法：必须是 32 字节随机密钥的 Base64 形式。"
    }
    return $candidate
}

function Set-MachineHmacKey([string]$key) {
    [Environment]::SetEnvironmentVariable($HmacEnvName, $key, [EnvironmentVariableTarget]::Machine)
    $env:KANBAN_HMAC_KEY = $key
}

if (-not (Test-Path $CollectorExe)) { throw "未找到 $CollectorExe，请先构建 Kanban.Collector（Debug）。" }
if (-not (Test-Path $SimExe)) { throw "未找到 $SimExe，请先构建 PlcSimulator（Debug）。" }

switch ($Action) {
    "Status" {
        Get-Service -Name $ServiceCollector, $ServiceSim -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-Table
        sc.exe qfailure $ServiceCollector 2>&1 | Out-Null
        sc.exe qfailure $ServiceSim 2>&1 | Out-Null
        exit 0
    }
}
if (-not (Test-Admin)) { throw "需要管理员权限：请右键 PowerShell 以管理员身份运行。" }

switch ($Action) {
    "Install" {
        $HmacKey = Resolve-HmacKey $HmacKey
        Write-Step "配置机器级 $HmacEnvName（不显示密钥内容）"
        Set-MachineHmacKey $HmacKey
        if (-not (Test-Path $DataRoot)) { New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null }
        $logDir = Join-Path $DataRoot "Config\Logs"
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

        # ── 1. Collector 服务（用官方脚本逻辑，避免依赖 ps1 路径）──
        Write-Step "安装 $ServiceCollector 服务"
        sc.exe create $ServiceCollector binPath= "`"$CollectorExe`"" start= auto DisplayName= "Kanban 采集服务" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc create Collector 失败（exit=$LASTEXITCODE），可能已存在，先 -Action Uninstall。" }
        $envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceCollector\Environment"
        if (-not (Test-Path $envKey)) { New-Item -Path $envKey -Force | Out-Null }
        Set-ItemProperty -Path $envKey -Name "KANBAN_DATA_DIR" -Value $DataRoot
        Set-ItemProperty -Path $envKey -Name $HmacEnvName -Value $HmacKey
        sc.exe failure $ServiceCollector reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        sc.exe start $ServiceCollector | Out-Null

        # ── 2. NSSM 下载（PlcSimulator 是控制台程序，需 NSSM 包装为服务）──
        if (-not (Test-Path $NssmExe)) {
            Write-Step "下载 NSSM（包装 PlcSimulator 为服务所需）"
            $nssmUrl = "https://nssm.cc/ci/nssm-2.24-101-g897c7ad.zip"
            $zip = Join-Path $env:TEMP "nssm.zip"
            Invoke-WebRequest -Uri $nssmUrl -OutFile $zip -UseBasicParsing
            $extract = Join-Path $env:TEMP "nssm-extract"
            if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
            Expand-Archive -Path $zip -DestinationPath $extract -Force
            $nssm32 = Get-ChildItem $extract -Recurse -Filter "nssm.exe" | Where-Object { $_.FullName -match "win32" } | Select-Object -First 1
            Copy-Item $nssm32.FullName $NssmExe -Force
        }

        # ── 3. PlcSimulator 服务（NSSM 包装，端口 4999 与 run-demo settings.json 的 PlcConfig.Port 一致；
        #        审查修复 2026-08-15：原 4998 与配置不一致，装完服务后 Collector 连不上模拟器）──
        Write-Step "安装 $ServiceSim 服务（NSSM，端口 4999）"
        & $NssmExe install $ServiceSim $SimExe 4999 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "NSSM install 失败（exit=$LASTEXITCODE）" }
        & $NssmExe set $ServiceSim AppDirectory (Split-Path $SimExe -Parent) | Out-Null
        & $NssmExe set $ServiceSim AppEnvironmentExtra "KANBAN_DATA_DIR=$DataRoot" "KANBAN_HMAC_KEY=$HmacKey" | Out-Null
        & $NssmExe set $ServiceSim AppStdout (Join-Path $DataRoot "Config\Logs\plcsim_service.log") | Out-Null
        & $NssmExe set $ServiceSim AppStderr (Join-Path $DataRoot "Config\Logs\plcsim_service_err.log") | Out-Null
        & $NssmExe set $ServiceSim AppExitAction Restart | Out-Null
        & $NssmExe set $ServiceSim AppRestartDelay 5000 | Out-Null
        & $NssmExe set $ServiceSim Start SERVICE_AUTO_START | Out-Null
        sc.exe failure $ServiceSim reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        & $NssmExe start $ServiceSim | Out-Null

        Write-Step "启动服务"
        Start-Sleep -Seconds 5
        sc.exe query $ServiceCollector
        sc.exe query $ServiceSim
        Write-Host ""
        Write-Host "已安装完成。数据目录: $DataRoot" -ForegroundColor Green
        Write-Host "验证: curl http://127.0.0.1:5129/health/ready 应为 Healthy" -ForegroundColor Green
    }
    "Uninstall" {
        Write-Step "卸载服务"
        sc.exe stop $ServiceSim 2>&1 | Out-Null; Start-Sleep -Seconds 2
        sc.exe delete $ServiceSim 2>&1 | Out-Null
        sc.exe stop $ServiceCollector 2>&1 | Out-Null; Start-Sleep -Seconds 2
        sc.exe delete $ServiceCollector 2>&1 | Out-Null
        Write-Host "已卸载。" -ForegroundColor Green
    }
    "Status" {
        Get-Service -Name $ServiceCollector, $ServiceSim -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-Table
        $machineKey = [Environment]::GetEnvironmentVariable($HmacEnvName, [EnvironmentVariableTarget]::Machine)
        Write-Host "机器 HMAC 密钥: $(if ([string]::IsNullOrWhiteSpace($machineKey)) { '未配置' } else { '已配置（不显示）' })"
        sc.exe qfailure $ServiceCollector 2>&1 | Out-Null
        sc.exe qfailure $ServiceSim 2>&1 | Out-Null
    }
}
