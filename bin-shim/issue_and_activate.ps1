$ErrorActionPreference = 'Stop'

# 1. 停止旧的 MainAPP 实例
foreach ($id in @(11340, 2636)) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1
Write-Output "cleaned old MainAPP instances"

# 2. 用历史回退密钥执行签发（永久授权，绑定机器码 STE3WVRO）
$env:KANBAN_HMAC_KEY = 'zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE='
Write-Output "issuing license..."
Set-Location 'D:\SourceCode\Kanban0817\Kanban'
$out = & dotnet run --project LicenseIssuer.CLI/LicenseIssuer.CLI.csproj -- issue --machine STE3WVRO 2>&1 | Out-String
Write-Output $out