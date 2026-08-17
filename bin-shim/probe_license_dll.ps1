param([string]$Dll)
$bytes = [IO.File]::ReadAllBytes($Dll)
$text = [Text.Encoding]::Unicode.GetString($bytes)
Write-Output ("HasOldFallbackKey: " + $text.Contains('zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE='))
Write-Output ("HasEnvVarName: " + $text.Contains('KANBAN_HMAC_KEY'))
Write-Output ("HasNewMsgBanned: " + $text.Contains('HMAC'))
$idx = $text.IndexOf('KANBAN')
if ($idx -ge 0) { Write-Output ("Context: " + $text.Substring($idx, 120)) }