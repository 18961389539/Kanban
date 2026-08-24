$ErrorActionPreference = 'Stop'
$root = 'D:\VisionTest\KANBAN\Kanban'
$targets = @('obj','bin')
$dirs = Get-ChildItem $root -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $targets -contains $_.Name }
$ok=0; $fail=0
foreach($d in $dirs){
    try {
        [System.IO.Directory]::Delete($d.FullName, $true)
        $ok++
    } catch {
        $fail++
        # try deleting files individually
        try {
            Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                try { [System.IO.File]::Delete($_.FullName) } catch {}
            }
            [System.IO.Directory]::Delete($d.FullName, $true)
        } catch {}
    }
}
Write-Output "dirs removed: $ok ; failed: $fail"
# also clean alt
foreach($a in @('D:\VisionTest\KANBAN\Kanban\_obj_alt','D:\VisionTest\KANBAN\Kanban\_bin_alt')){
    if(Test-Path $a){ try{[System.IO.Directory]::Delete($a,$true)}catch{} }
}
Write-Output "cleanup done"
