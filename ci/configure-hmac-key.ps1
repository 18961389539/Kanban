# Configure the external HMAC key required by MainAPP and the service installers.
# Run elevated when writing the machine environment.
#
# Existing licensed deployment:
#   .\configure-hmac-key.ps1 -HmacKey "<the same 32-byte Base64 key used by LicenseIssuer>"
# New development/demo machine without existing license data:
#   .\configure-hmac-key.ps1 -Generate

param(
    [string]$HmacKey = "",
    [switch]$Generate
)

$ErrorActionPreference = "Stop"
$EnvName = "KANBAN_HMAC_KEY"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) { throw "Configuring the machine HMAC key requires an elevated PowerShell." }
if ($Generate -and -not [string]::IsNullOrWhiteSpace($HmacKey)) {
    throw "-Generate and -HmacKey cannot be used together."
}

if ([string]::IsNullOrWhiteSpace($HmacKey)) {
    if (-not $Generate) {
        $HmacKey = $env:KANBAN_HMAC_KEY
    }
    if ([string]::IsNullOrWhiteSpace($HmacKey)) {
        $HmacKey = [Environment]::GetEnvironmentVariable($EnvName, [EnvironmentVariableTarget]::Machine)
    }
}

if ([string]::IsNullOrWhiteSpace($HmacKey) -and $Generate) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) }
    finally { $rng.Dispose() }
    $HmacKey = [Convert]::ToBase64String($bytes)
}

if ([string]::IsNullOrWhiteSpace($HmacKey)) {
    throw "No HMAC key was provided. Existing licensed deployments must use the original LicenseIssuer key; add -Generate only for a new development/demo machine."
}

$HmacKey = $HmacKey.Trim()
try { $decoded = [Convert]::FromBase64String($HmacKey) }
catch { throw "$EnvName is not valid Base64." }
if ($decoded.Length -ne 32) {
    throw "$EnvName must decode to exactly 32 bytes." 
}

[Environment]::SetEnvironmentVariable($EnvName, $HmacKey, [EnvironmentVariableTarget]::Machine)
$env:KANBAN_HMAC_KEY = $HmacKey

Write-Host "Machine-level $EnvName configured (key value not displayed)." -ForegroundColor Green
Write-Host "Restart MainAPP and any PowerShell/Explorer process that launched it." -ForegroundColor Yellow
if ($Generate) {
    Write-Host "WARNING: this generated key is only for a new development/demo machine; existing licenses require the original signing key." -ForegroundColor Yellow
}