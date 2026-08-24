# Fix: Collector service (LocalSystem) never had a config under systemprofile
# -> it always used defaults (Mitsubishi/127.0.0.1) and could never reach the PLC.
# Creates Config/settings.json for the service account and restarts the service.
# Run elevated. Logs to gui-test-screenshots/svc_cfg_fix.log (UTF-8).
$ErrorActionPreference = 'Continue'
$log = 'D:\SourceCode\Kanban0731\Kanban\gui-test-screenshots\svc_cfg_fix.log'
$lines = New-Object System.Collections.Generic.List[string]
function Log($m) { $lines.Add($m) }

# 1) Seed config for the LocalSystem service account
$dst = 'C:\Windows\System32\config\systemprofile\AppData\Roaming\Kanban\Config'
$src = 'D:\SourceCode\Kanban0731\Kanban\run-demo\data\Config\settings.json'
try {
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Copy-Item $src (Join-Path $dst 'settings.json') -Force
    $check = Get-Content (Join-Path $dst 'settings.json') -Raw
    if ($check -match '"IpAddress": "127.0.0.1"' -and $check -match '"Brand": 1') {
        Log 'OK config seeded (Mitsubishi/127.0.0.1)'
    } else {
        Log 'FAIL config content mismatch'
    }
} catch { Log "FAIL config copy: $($_.Exception.Message)" }

# 2) Stop collector
try {
    $null = sc.exe stop KanbanCollector
    Start-Sleep -Seconds 3
    Log "after stop: $((Get-Service KanbanCollector).Status)"
} catch { Log "FAIL stop: $($_.Exception.Message)" }

# 3) Start collector
try {
    $null = sc.exe start KanbanCollector
    Start-Sleep -Seconds 6
    Log "after start: $((Get-Service KanbanCollector).Status)"
} catch { Log "FAIL start: $($_.Exception.Message)" }

# 4) New PID
try {
    $svc = Get-CimInstance Win32_Service -Filter 'Name="KanbanCollector"'
    Log "collector PID: $($svc.ProcessId)"
} catch { Log "FAIL pid query: $($_.Exception.Message)" }

[System.IO.File]::WriteAllText($log, ($lines -join "`r`n"), [System.Text.Encoding]::UTF8)
