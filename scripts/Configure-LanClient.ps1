[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server-info.json') -Raw | ConvertFrom-Json
$certificateFile = Join-Path $PSScriptRoot 'FocusTime-LAN.cer'
$certificate = Get-PfxCertificate -FilePath $certificateFile
if ($certificate.HasPrivateKey -or $certificate.Thumbprint -ne $settings.CertificateThumbprint) {
    throw 'The public certificate does not match server-info.json.'
}
$uri = $null
if (-not [Uri]::TryCreate([string]$settings.ApiBaseUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'server-info.json must contain an HTTPS API address.'
}
if (-not (Test-Path -LiteralPath ('Cert:\CurrentUser\Root\' + $certificate.Thumbprint))) {
    if ($VerifyOnly) { throw 'The certificate is not trusted yet. Run this script without -VerifyOnly to configure it.' }
    Import-Certificate -FilePath $certificateFile -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
}
if (-not $VerifyOnly) {
    [Environment]::SetEnvironmentVariable('FOCUSTIME_API_BASE_URL', $uri.AbsoluteUri, 'User')
    $env:FOCUSTIME_API_BASE_URL = $uri.AbsoluteUri
    Write-Output "Client API configured: $($uri.AbsoluteUri)"
} else { Write-Output "Checking API: $($uri.AbsoluteUri)" }
Write-Output "Certificate SHA-1 thumbprint: $($certificate.Thumbprint)"
try {
    $health = Invoke-RestMethod ($uri.AbsoluteUri + 'health/ready') -TimeoutSec 10
    if ($health.status -ne 'healthy') { throw 'The API database is not ready.' }
    Write-Output 'HTTPS certificate validation and API readiness passed.'
} catch {
    Write-Warning 'Settings saved, but the API is unreachable. Check Wi-Fi isolation, server IP, proxy and firewall.'
    throw
}
if (-not $VerifyOnly) {
    Write-Output 'Close the client and its launcher, then reopen them so the new environment setting is inherited.'
    Write-Output "For a source checkout, run scripts/Start-FocusApp.ps1 from this PowerShell. The client local service is still required."
}
