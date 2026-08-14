# Seed LocalSystem service data dir from user APPDATA instance, restart collector.
# Safe to run in an elevated shell; logs to gui-test-screenshots/svc_data_fix.log (UTF-8).
$ErrorActionPreference = 'Continue'
$log = 'D:\SourceCode\Kanban0731\Kanban\gui-test-screenshots\svc_data_fix.log'
$lines = New-Object System.Collections.Generic.List[string]
function Log($m) { $lines.Add($m) }

$srcDir = 'C:\Users\35953\AppData\Roaming\Kanban\Config'
$dstDir = 'C:\Windows\System32\config\systemprofile\AppData\Roaming\Kanban\Config'
$files = @(
    'devices.json', 'recipes.json', 'users.json', 'baselines.json',
    'work_orders.db', 'production_logs.db', 'status_transitions.db',
    'alarm_events.db', 'last_query.json'
)

try {
    $null = sc.exe stop KanbanCollector
    Start-Sleep -Seconds 3
    Log "after stop: $((Get-Service KanbanCollector).Status)"
} catch { Log "FAIL stop: $($_.Exception.Message)" }

New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
foreach ($f in $files) {
    $s = Join-Path $srcDir $f
    $d = Join-Path $dstDir $f
    try {
        if (Test-Path $s) {
            Copy-Item $s $d -Force
            Log "OK $f ($((Get-Item $d).Length) bytes)"
        } else { Log "skip $f (no source)" }
    } catch { Log "FAIL ${f}: $($_.Exception.Message)" }
}

try {
    $null = sc.exe start KanbanCollector
    Start-Sleep -Seconds 6
    $svc = Get-CimInstance Win32_Service -Filter 'Name="KanbanCollector"'
    Log "after start: $((Get-Service KanbanCollector).Status) PID: $($svc.ProcessId)"
} catch { Log "FAIL start: $($_.Exception.Message)" }

[System.IO.File]::WriteAllText($log, ($lines -join "`r`n"), [System.Text.Encoding]::UTF8)
