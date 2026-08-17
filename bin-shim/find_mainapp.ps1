Get-CimInstance Win32_Process -Filter "Name='MainAPP.exe' OR Name='dotnet.exe'" |
    Where-Object { $_.Name -eq 'MainAPP.exe' -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'MainAPP\.dll') } |
    Select-Object Name, ProcessId, @{N='Cmd';E={ if ($_.CommandLine.Length -gt 90) { $_.CommandLine.Substring(0,90) } else { $_.CommandLine } }} |
    Format-List