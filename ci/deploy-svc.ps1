# Deploy: replace Collector service binaries (new build with 10MB receive limit)
# and WASM wwwroot (brand text fix), then restart the service.
# Run elevated. Logs to gui-test-screenshots/svc_deploy.log (UTF-8).
$ErrorActionPreference = 'Continue'
$log = 'D:\SourceCode\Kanban0731\Kanban\gui-test-screenshots\svc_deploy.log'
$lines = New-Object System.Collections.Generic.List[string]
function Log($m) { $lines.Add($m) }

$svcBin = 'D:\SourceCode\Kanban0731\Kanban\Kanban.Collector\bin\Debug\net10.0-windows'
$newBin = 'D:\SourceCode\Kanban0731\Kanban\.tmpbin\Debug\net10.0-windows'
$newWeb = 'D:\SourceCode\Kanban0731\Kanban\.staging\web_publish_new'

try {
    $null = sc.exe stop KanbanCollector
    Start-Sleep -Seconds 4
    Log "after stop: $((Get-Service KanbanCollector).Status)"
} catch { Log "FAIL stop: $($_.Exception.Message)" }

# 1) Binaries (excluding wwwroot which is replaced separately)
try {
    Get-ChildItem $newBin -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $svcBin $_.Name) -Force
    }
    Log "OK binaries copied from .tmpbin ($((Get-ChildItem $newBin -File).Count) files)"
} catch { Log "FAIL binaries: $($_.Exception.Message)" }

# 2) WASM wwwroot (staging -> service wwwroot)
try {
    $svcWww = Join-Path $svcBin 'wwwroot'
    if (Test-Path $svcWww) { Remove-Item $svcWww -Recurse -Force }
    Copy-Item $newWeb $svcWww -Recurse -Force
    Log "OK wwwroot copied (has index.html: $(Test-Path (Join-Path $svcWww 'index.html')))"
} catch { Log "FAIL wwwroot: $($_.Exception.Message)" }

try {
    $null = sc.exe start KanbanCollector
    Start-Sleep -Seconds 8
    $svc = Get-CimInstance Win32_Service -Filter 'Name="KanbanCollector"'
    Log "after start: $((Get-Service KanbanCollector).Status) PID: $($svc.ProcessId)"
} catch { Log "FAIL start: $($_.Exception.Message)" }

[System.IO.File]::WriteAllText($log, ($lines -join "`r`n"), [System.Text.Encoding]::UTF8)
