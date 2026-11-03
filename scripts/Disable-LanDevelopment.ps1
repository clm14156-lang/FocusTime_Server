[CmdletBinding()]
param([string]$ClientProjectRoot = 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01')

$ErrorActionPreference = 'Stop'
$serverRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clientRoot = [IO.Path]::GetFullPath($ClientProjectRoot)
$runtime = Join-Path $clientRoot '.codex-build/local-login-server'
$lanFile = Join-Path $runtime 'lan-settings.json'
if (-not (Test-Path -LiteralPath $lanFile)) { Write-Output 'LAN mode is already disabled.'; return }
$lan = Get-Content -LiteralPath $lanFile -Raw | ConvertFrom-Json
& (Join-Path $clientRoot 'scripts/Stop-LocalLoginServer.ps1')
foreach ($name in @('FocusTime-LAN-HTTPS', 'FocusTime-LAN-PostgreSQL')) {
    Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
$hbaFile = Join-Path $runtime 'postgres/pg_hba.conf'
$hba = Get-Content -LiteralPath $hbaFile -Raw
$hba = [regex]::Replace($hba, '(?ms)^# BEGIN FOCUSTIME LAN TEST\r?\n.*?^# END FOCUSTIME LAN TEST\r?\n?', '')
[IO.File]::WriteAllText($hbaFile, $hba, [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $lanFile
& (Join-Path $clientRoot 'scripts/Start-LocalLoginServer.ps1') -ServerRoot $serverRoot -DatabasePort ([int]$lan.DatabasePort)
Write-Output 'LAN listeners and managed firewall rules removed. Accounts and records are retained; localhost API remains available.'
