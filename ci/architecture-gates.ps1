[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $mainAppSources = Get-ChildItem "MainAPP" -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" }

    $legacyHookMatches = $mainAppSources |
        Select-String -Pattern 'Remote(Persistence|Rollback|BackupAvailability|Upsert|Delete)Hook'
    if ($legacyHookMatches) {
        $legacyHookMatches | ForEach-Object { Write-Error $_.ToString() }
        throw 'MainAPP contains deprecated Remote Hook references.'
    }

    $privateAsyncVoidMatches = $mainAppSources |
        Select-String -Pattern '\bprivate\s+async\s+void\b'
    if ($privateAsyncVoidMatches) {
        $privateAsyncVoidMatches | ForEach-Object { Write-Error $_.ToString() }
        throw 'MainAPP contains private async void business methods.'
    }

    Write-Host "architecture gates passed"
}
finally {
    Pop-Location
}