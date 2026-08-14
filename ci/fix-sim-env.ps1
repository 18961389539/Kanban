# 仿真环境修复脚本（审查修复 2026-08-14）
# 需管理员权限：停止演示服务 → 刷新 Collector 托管的 WASM（修复报警中心崩溃的版本碎片）→ 重启服务。
# 磁盘配置已确认：%APPDATA%\Kanban\Config\settings.json 的 PLC IP 已是 127.0.0.1:4999（可达），
# 服务进程内存仍为旧值（192.168.1.2 不可达）——重启即生效，无需改配置。
# 用法：以管理员身份运行  powershell -ExecutionPolicy Bypass -File ci\fix-sim-env.ps1

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Write-Host '==> 1/4 停止演示服务'
try { Stop-Service KanbanCollector -Force -ErrorAction Stop } catch { Write-Warning "停止 KanbanCollector 失败: $($_.Exception.Message)" }
try { Stop-Service KanbanPlcSimulator -Force -ErrorAction Stop } catch { Write-Warning "停止 KanbanPlcSimulator 失败: $($_.Exception.Message)" }
Start-Sleep -Seconds 2

try {
    Write-Host '==> 2/4 刷新 Collector 托管的 wwwroot（优先使用预置暂存，缺省时现场发布）'
    $repo = Split-Path -Parent $PSScriptRoot
    $staged = Join-Path $repo '.staging\web_publish\wwwroot'
    $srcWww = $null
    if (Test-Path $staged) {
        $srcWww = $staged
        Write-Host "    使用预置暂存: $srcWww"
    } else {
        $webTemp = Join-Path $env:TEMP 'kanban_web_publish_refresh'
        if (Test-Path $webTemp) { Remove-Item $webTemp -Recurse -Force }
        & dotnet publish (Join-Path $repo 'Kanban.Web\Kanban.Web.csproj') -c Debug -o $webTemp --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Kanban.Web publish 失败' }
        $srcWww = Join-Path $webTemp 'wwwroot'
    }
    if (-not (Test-Path $srcWww)) { throw "缺少 wwwroot: $srcWww" }

    $collRoot = Join-Path $repo 'Kanban.Collector\bin\Debug\net10.0-windows'
    $dstWww = Join-Path $collRoot 'wwwroot'
    if (Test-Path $dstWww) { Remove-Item $dstWww -Recurse -Force }
    Copy-Item $srcWww $dstWww -Recurse -Force
    Write-Host "    wwwroot 已刷新: $dstWww"
}
finally {
    Write-Host '==> 3/4 重启演示服务（磁盘配置 127.0.0.1:4999 重启后生效）'
    try { Start-Service KanbanPlcSimulator -ErrorAction Stop } catch { Write-Warning "启动 KanbanPlcSimulator 失败: $($_.Exception.Message)" }
    try { Start-Service KanbanCollector -ErrorAction Stop } catch { Write-Warning "启动 KanbanCollector 失败: $($_.Exception.Message)" }
    Start-Sleep -Seconds 8
}

Write-Host '==> 4/4 验证'
try {
    $health = (Invoke-WebRequest -Uri 'http://localhost:5129/healthz' -UseBasicParsing -TimeoutSec 5).StatusCode
    Write-Host "    healthz: $health"
    $root = (Invoke-WebRequest -Uri 'http://localhost:5129/' -UseBasicParsing -TimeoutSec 5).StatusCode
    Write-Host "    root:    $root"
} catch {
    Write-Warning "健康检查失败: $($_.Exception.Message)"
}
Write-Host '完成。随后请在非管理员终端运行： node ci/gui-review.mjs  和   node ci/wasm-smoke.js'
