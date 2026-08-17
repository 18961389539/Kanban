$ErrorActionPreference = 'Stop'
$key = 'STE3W-VRO77-75TOA-SMFZY-WQXLC-CU2AQ-ETVBN-CGJ'
$env:KANBAN_HMAC_KEY = 'zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE='

# 1. 校验激活码与机器码绑定
Set-Location 'D:\SourceCode\Kanban0817\Kanban'
$verify = & dotnet "LicenseIssuer.CLI/bin/Debug/net10.0-windows/LicenseIssuer.dll" verify --key $key --machine STE3WVRO 2>&1 | Out-String
Write-Output "=== verify ==="
Write-Output $verify

# 2. 构造 LicenseInfo JSON 并 DPAPI 加密写入 %APPDATA%\Kanban\license.dat
$licenseInfo = @{
    MachineCodeHash = 'STE3WVRO'
    ExpireDate      = $null
    ActivatedAt     = [DateTime]::UtcNow
    ProductKey      = $key
} | ConvertTo-Json

Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Security.dll"
$plainBytes = [Text.Encoding]::UTF8.GetBytes($licenseInfo)
$encrypted  = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$target = Join-Path $env:APPDATA 'Kanban\license.dat'
[IO.File]::WriteAllBytes($target, $encrypted)
Write-Output "license.dat written: $target ($($encrypted.Length) bytes)"