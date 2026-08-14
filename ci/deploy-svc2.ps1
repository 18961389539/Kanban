# Deploy retry: fix wwwroot layer (site content lives in web_publish_new/wwwroot/),
# retry binary copy until old process releases locks, then restart service.
# Run elevated. Logs to gui-test-screenshots/svc_deploy2.log (UTF-8).
$ErrorActionPreference = 'Continue'
$log = 'D:\SourceCode\Kanban0731\Kanban\gui-test-screenshots\svc_deploy2.log'
$lines = New-Object System.Collections.Generic.List[string]
function Log($m) { $lines.Add($m) }

$svcBin = 'D:\SourceCode\Kanban0731\Kanban\Kanban.Collector\bin\Debug\net10.0-windows'
$newBin = 'D:\SourceCode\Kanban0731\Kanban\.tmpbin\Debug\net10.0-windows'
$newWebRoot = 'D:\SourceCode\Kanban0731\Kanban\.staging\web_publish_new\wwwroot'

try {
    $null = sc.exe stop KanbanCollector
    Start-Sleep -Seconds 5
    Log "after stop: $((Get-Service KanbanCollector).Status)"
} catch { Log "FAIL stop: $($_.Exception.Message)" }

# 1) Binaries with retry (old process may hold locks briefly after stop)
$ok = $false
for ($i = 1; $i -le 5; $i++) {
    $failed = @()
    Get-ChildItem $newBin -File | ForEach-Object {
        try { Copy-Item $_.FullName (Join-Path $svcBin $_.Name) -Force }
        catch { $failed += $_.Name }
    }
    if ($failed.Count -eq 0) { $ok = $true; Log "OK binaries copied on attempt $i"; break }
    Log "attempt $i: $($failed.Count) locked files: $($failed -join ', ')"
    Start-Sleep -Seconds 4
}
if (-not $ok) { Log "FAIL binaries still locked after retries" }

# 2) wwwroot: site content is in publish wwwroot/ subdir
try {
    $svcWww = Join-Path $svcBin 'wwwroot'
    if (Test-Path $svcWww) { Remove-Item $svcWww -Recurse -Force }
    Copy-Item $newWebRoot $svcWww -Recurse -Force
    Log "OK wwwroot (index.html: $(Test-Path (Join-Path $svcWww 'index.html')))"
} catch { Log "FAIL wwwroot: $($_.Exception.Message)" }

try {
    $null = sc.exe start KanbanCollector
    Start-Sleep -Seconds 8
    $svc = Get-CimInstance Win32_Service -Filter 'Name="KanbanCollector"'
    Log "after start: $((Get-Service KanbanCollector).Status) PID: $($svc.ProcessId)"
} catch { Log "FAIL start: $($_.Exception.Message)" }

[System.IO.File]::WriteAllText($log, ($lines -join "`r`n"), [System.Text.Encoding]::UTF8)
