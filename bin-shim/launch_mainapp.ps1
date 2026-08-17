# 持久化 KANBAN_HMAC_KEY 到用户级环境变量（setx 只影响后续进程，本进程显式设置）
$keyValue = 'zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE='
[Environment]::SetEnvironmentVariable('KANBAN_HMAC_KEY', $keyValue, 'User')
Write-Output ("SetEnvironmentVariable User: " + [Environment]::GetEnvironmentVariable('KANBAN_HMAC_KEY', 'User'))

# 启动 MainAPP（继承本进程环境）
$env:KANBAN_HMAC_KEY = $keyValue
Start-Process -FilePath 'D:\SourceCode\Kanban0817\Kanban\MainAPP\bin\Debug\net10.0-windows\MainAPP.exe' `
    -WorkingDirectory 'D:\SourceCode\Kanban0817\Kanban\MainAPP\bin\Debug\net10.0-windows'
Write-Output 'MainAPP launched'