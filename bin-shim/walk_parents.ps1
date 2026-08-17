$target = Get-CimInstance Win32_Process -Filter "ProcessId=11340"
$p = $target
$depth = 0
while ($p -and $depth -lt 6) {
    Write-Output ("$depth : PID=$($p.ProcessId) PPID=$($p.ParentProcessId) NAME=$($p.Name)")
    Write-Output ("       CMD=$($p.CommandLine)")
    $p = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $p.ParentProcessId) -ErrorAction SilentlyContinue
    $depth++
}