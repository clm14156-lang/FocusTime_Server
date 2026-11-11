#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalServices.Common.ps1')
$settings = Get-LocalServiceSettings
if (Test-LocalDatabaseRunning $settings) {
    Write-Output "PostgreSQL is already running: $($settings.DatabaseData)"
    return
}
if (-not $PSCmdlet.ShouldProcess($settings.DatabaseData, 'Start PostgreSQL')) { return }

$addresses = @('127.0.0.1')
$lanFile = Join-Path $settings.ClientLocalRoot 'lan-settings.json'
if (Test-Path -LiteralPath $lanFile -PathType Leaf) {
    $lan = Get-Content -LiteralPath $lanFile -Raw | ConvertFrom-Json
    $assigned = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        ForEach-Object { $_.Address.ToString() }
    if ($lan.Address -in $assigned -and $lan.Address -ne '127.0.0.1') {
        $addresses += [string]$lan.Address
    }
}

$listen = $addresses -join ','
$log = Join-Path $settings.ClientLocalRoot 'postgres.log'
& $settings.PgCtl -D $settings.DatabaseData -l $log -o "-h $listen -p $($settings.DatabasePort)" -w start
if ($LASTEXITCODE -ne 0) { throw "PostgreSQL failed to start. See $log." }
Write-Output "PostgreSQL is listening on $listen (port $($settings.DatabasePort))."
