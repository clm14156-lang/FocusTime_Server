#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalServices.Common.ps1')
$settings = Get-LocalServiceSettings
if (-not (Test-LocalDatabaseRunning $settings)) {
    Write-Output 'PostgreSQL is already stopped.'
    return
}
if (-not $PSCmdlet.ShouldProcess($settings.DatabaseData, 'Stop PostgreSQL')) { return }

& $settings.PgCtl -D $settings.DatabaseData -m fast -w stop
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL failed to stop.' }
