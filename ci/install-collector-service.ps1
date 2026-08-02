# 安装/卸载 Kanban.Collector 为 Windows 服务（开机自启 + 崩溃自动重启）
#
# 用法（需管理员权限）：
#   .\install-collector-service.ps1 -Action Install -DataRoot "D:\KanbanData"
#   .\install-collector-service.ps1 -Action Status
#   .\install-collector-service.ps1 -Action Uninstall
#
# 关键说明：
#   1. -DataRoot 必填：服务账户（LocalSystem）的 %APPDATA% 是 systemprofile，
#      与 MainAPP（当前用户）不在同一目录。必须显式指定数据目录，
#      且该目录须与 MainAPP 部署机使用的一致（settings.json/devices.json/4 个 .db）。
#   2. 崩溃自动重启策略：5s / 10s / 30s 三次重试后复位（reset=86400 秒）。
#   3. 卸载服务前请先停止采集（sc.exe stop KanbanCollector）。

param(
    [ValidateSet("Install", "Uninstall", "Status")]
    [string]$Action = "Install",
    [string]$DataRoot = "",
    [string]$CollectorExe = ""
)

$ErrorActionPreference = "Stop"
$ServiceName = "KanbanCollector"

# 未指定时按约定路径自动查找（Debug/Release 均可）
if (-not $CollectorExe) {
    $candidates = @(
        Join-Path $PSScriptRoot "..\Kanban.Collector\bin\Release\net10.0-windows\Kanban.Collector.exe",
        Join-Path $PSScriptRoot "..\Kanban.Collector\bin\Debug\net10.0-windows\Kanban.Collector.exe"
    )
    $CollectorExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

switch ($Action) {
    "Install" {
        if (-not (Test-Admin)) { throw "安装服务需要管理员权限，请用管理员 PowerShell 运行。" }
        if (-not $CollectorExe -or -not (Test-Path $CollectorExe)) {
            throw "未找到 Kanban.Collector.exe，请先 dotnet build 或 -CollectorExe 指定路径。"
        }
        if (-not $DataRoot) {
            throw "-DataRoot 必填：服务账户与 MainAPP 用户 %APPDATA% 不同，必须显式指定共用数据目录。"
        }
        $DataRoot = [IO.Path]::GetFullPath($DataRoot)
        if (-not (Test-Path $DataRoot)) { New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null }

        Write-Step "创建服务 $ServiceName"
        sc.exe create $ServiceName binPath= "`"$CollectorExe`"" start= auto DisplayName= "Kanban 采集服务" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc create 失败（exit=$LASTEXITCODE），服务可能已存在，先 -Action Uninstall。" }

        # 服务环境变量：让 CollectorPaths 的 KANBAN_DATA_DIR 指向指定数据目录
        Write-Step "写入服务环境变量 KANBAN_DATA_DIR=$DataRoot"
        $envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName\Environment"
        if (-not (Test-Path $envKey)) { New-Item -Path $envKey -Force | Out-Null }
        Set-ItemProperty -Path $envKey -Name "KANBAN_DATA_DIR" -Value $DataRoot

        # 崩溃自动重启：5s/10s/30s 三次，24 小时内不复位
        Write-Step "配置崩溃自动重启策略"
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

        Write-Step "启动服务"
        sc.exe start $ServiceName | Out-Null
        Start-Sleep -Seconds 3
        sc.exe query $ServiceName
        Write-Host ""
        Write-Host "已安装。日志：$DataRoot\Config\Logs\collector_.log" -ForegroundColor Green
        Write-Host "注意：MainAPP 屏端 settings.json 的 CollectorHubUrl 需指向本机，DataMode 设为 Remote(1)。" -ForegroundColor Yellow
    }
    "Uninstall" {
        if (-not (Test-Admin)) { throw "卸载服务需要管理员权限。" }
        Write-Step "停止并删除服务 $ServiceName"
        sc.exe stop $ServiceName | Out-Null
        Start-Sleep -Seconds 2
        sc.exe delete $ServiceName | Out-Null
        Write-Host "已卸载。" -ForegroundColor Green
    }
    "Status" {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc) { Write-Host "服务 $ServiceName 未安装。" -ForegroundColor Yellow; exit 0 }
        Write-Host "服务状态: $($svc.Status)"
        $envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName\Environment"
        if (Test-Path $envKey) {
            $dataRoot = (Get-ItemProperty -Path $envKey -Name "KANBAN_DATA_DIR" -ErrorAction SilentlyContinue).KANBAN_DATA_DIR
            Write-Host "数据目录: $dataRoot"
        }
        sc.exe qfailure $ServiceName
    }
}
