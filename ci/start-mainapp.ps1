# Start MainAPP with the externally supplied HMAC key when one is available.
# The key is never written to the publish directory by this script.
# A machine without an activation key can still start MainAPP and use its
# current-user DPAPI protected trial state.

param(
    [switch]$PersistMachine
)

$ErrorActionPreference = "Stop"
$EnvName = "KANBAN_HMAC_KEY"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-HmacKey([string]$candidate) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $false }
    try { $decoded = [Convert]::FromBase64String($candidate.Trim()) }
    catch { return $false }
    return $decoded.Length -eq 32
}

function Read-HmacKeyMasked {
    $secureValue = Read-Host "Enter the 32-byte Base64 KANBAN_HMAC_KEY" -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Get-HmacKey {
    $candidate = $env:KANBAN_HMAC_KEY
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable($EnvName, [EnvironmentVariableTarget]::User)
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable($EnvName, [EnvironmentVariableTarget]::Machine)
    }

    if (Test-HmacKey $candidate) {
        return $candidate.Trim()
    }

    if (-not $PersistMachine) {
        Write-Host "KANBAN_HMAC_KEY is not configured on this computer." -ForegroundColor Yellow
        Write-Host "MainAPP will start without HMAC and use the 30-day trial when no license is installed." -ForegroundColor Yellow
        return $null
    }

    Write-Host "KANBAN_HMAC_KEY is required when -PersistMachine is used." -ForegroundColor Yellow
    Write-Host "Use the same key that was used by LicenseIssuer. A new key will not validate existing activation codes." -ForegroundColor Yellow
    $candidate = Read-HmacKeyMasked
    if (-not (Test-HmacKey $candidate)) {
        throw "$EnvName must be valid Base64 that decodes to exactly 32 bytes."
    }
    return $candidate.Trim()
}

$key = Get-HmacKey
if ($PersistMachine) {
    if (-not (Test-Admin)) { throw "Persisting the HMAC key at machine scope requires an elevated PowerShell." }
    [Environment]::SetEnvironmentVariable($EnvName, $key, [EnvironmentVariableTarget]::Machine)
    Write-Host "Machine-level $EnvName configured (key value not displayed)." -ForegroundColor Green
}

$publishedRoot = $PSScriptRoot
$mainAppExe = Join-Path $publishedRoot "MainAPP\MainAPP.exe"
$mainAppDll = Join-Path $publishedRoot "MainAPP\MainAPP.dll"
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.UseShellExecute = $false
if (-not [string]::IsNullOrWhiteSpace($key)) {
    $startInfo.EnvironmentVariables[$EnvName] = $key
}
else {
    $startInfo.EnvironmentVariables.Remove($EnvName)
}

if (Test-Path $mainAppExe) {
    $startInfo.FileName = $mainAppExe
    $startInfo.WorkingDirectory = Split-Path $mainAppExe -Parent
}
elseif (Test-Path $mainAppDll) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
    if (-not (Test-Path $dotnet)) { throw "MainAPP.exe was not found and dotnet is unavailable." }
    $startInfo.FileName = $dotnet
    $startInfo.Arguments = '"' + $mainAppDll + '"'
    $startInfo.WorkingDirectory = Split-Path $mainAppDll -Parent
}
else {
    throw "Published MainAPP was not found under $publishedRoot\MainAPP."
}

[System.Diagnostics.Process]::Start($startInfo) | Out-Null
Write-Host "MainAPP started." -ForegroundColor Green