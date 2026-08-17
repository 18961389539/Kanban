# Write license.dat for machine STE3WVRO, permanent, signed with the shared HMAC key.
# NOTE: ActivatedAt must be pre-formatted ISO-8601 string because PS5.1 ConvertTo-Json
#       emits the private \/Date(...)\/ format which System.Text.Json cannot parse.

$ErrorActionPreference = 'Stop'
$key = 'STE3W-VRO77-75TOA-SMFZY-WQXLC-CU2AQ-ETVBN-CGJ'
$env:KANBAN_HMAC_KEY = 'zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE='

Set-Location 'D:\SourceCode\Kanban0817\Kanban'
$verify = & dotnet "LicenseIssuer.CLI/bin/Debug/net10.0-windows/LicenseIssuer.dll" verify --key $key --machine STE3WVRO 2>&1 | Out-String
Write-Output '=== verify ==='
Write-Output $verify

Add-Type -Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Security.dll"
$licenseInfo = [ordered]@{
    MachineCodeHash = 'STE3WVRO'
    ExpireDate      = $null
    ActivatedAt     = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture)
    ProductKey      = $key
} | ConvertTo-Json

$plainBytes = [Text.Encoding]::UTF8.GetBytes($licenseInfo)
$encrypted  = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$target = Join-Path $env:APPDATA 'Kanban\license.dat'
[IO.File]::WriteAllBytes($target, $encrypted)

# Round-trip self check
$back = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($target), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
Write-Output '=== roundtrip ==='
Write-Output ([Text.Encoding]::UTF8.GetString($back))