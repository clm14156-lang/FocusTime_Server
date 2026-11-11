#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet(5080, 5082)][int]$Port = 5082)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalServices.Common.ps1')
$settings = Get-LocalServiceSettings
$api = Get-LocalApiSettings $settings $Port
$process = Get-LocalApiProcess $settings $api
if (-not $process) {
    Write-Output "FocusTime API is already stopped on port $Port."
    return
}
if (-not $PSCmdlet.ShouldProcess("$($api.BaseUrl) (PID $($process.Id))", 'Stop FocusTime API')) { return }

Stop-Process -InputObject $process
if (-not $process.WaitForExit(10000)) { throw "FocusTime API on port $Port did not exit." }
Write-Output "FocusTime API stopped on port $Port."
