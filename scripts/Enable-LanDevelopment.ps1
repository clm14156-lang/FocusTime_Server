[CmdletBinding()]
param(
    [string]$ClientProjectRoot = 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01',
    [string]$InterfaceAlias = 'WLAN',
    [ValidateRange(1024, 65535)][int]$HttpsPort = 5081,
    [ValidateRange(1024, 65535)][int]$DatabasePort = 55432
)

$ErrorActionPreference = 'Stop'
$serverRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clientRoot = [IO.Path]::GetFullPath($ClientProjectRoot)
$runtime = Join-Path $clientRoot '.codex-build/local-login-server'
$data = Join-Path $runtime 'postgres'
$lanFile = Join-Path $runtime 'lan-settings.json'
$hbaFile = Join-Path $data 'pg_hba.conf'
$bundle = Join-Path $serverRoot '.artifacts/lan-client'
foreach ($required in @((Join-Path $data 'PG_VERSION'), (Join-Path $runtime 'database-password.dpapi'),
    (Join-Path $clientRoot 'scripts/Start-LocalLoginServer.ps1'), $hbaFile)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Existing test server file missing: $required" }
}
$starter = Get-Content -LiteralPath (Join-Path $clientRoot 'scripts/Start-LocalLoginServer.ps1') -Raw
if (-not $starter.Contains('lan-settings.json')) {
    throw 'Apply integrations/windowBlocked01/lan-startup.patch to the client startup script first.'
}
$address = @(Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' })
if ($address.Count -ne 1) { throw 'Select a physical LAN adapter with one preferred IPv4 address.' }
$ip = $address[0].IPAddress
$prefix = [int]$address[0].PrefixLength
$bytes = [Net.IPAddress]::Parse($ip).GetAddressBytes()
if (-not ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
    ($bytes[0] -eq 192 -and $bytes[1] -eq 168)) -or $prefix -lt 16 -or $prefix -gt 30) {
    throw 'Use a private LAN IPv4 subnet with a prefix between /16 and /30.'
}
$network = New-Object byte[] 4
for ($index = 0; $index -lt 4; $index++) {
    $bits = [Math]::Max(0, [Math]::Min(8, $prefix - 8 * $index))
    $mask = if ($bits -eq 0) { 0 } else { 256 - [Math]::Pow(2, 8 - $bits) }
    $network[$index] = $bytes[$index] -band [int]$mask
}
$subnet = ($network -join '.') + '/' + $prefix
$subject = 'FocusTime-LAN-' + $ip
$apiUrl = 'https://{0}:{1}/' -f $ip, $HttpsPort
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
$cert = Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object {
    $_.Subject -eq ('CN=' + $subject) -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7)
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $san = '2.5.29.17={text}DNS=localhost&DNS=' + $env:COMPUTERNAME + '&IPAddress=127.0.0.1&IPAddress=' + $ip
    $cert = New-SelfSignedCertificate -Type SSLServerAuthentication -Subject ('CN=' + $subject) `
        -TextExtension @($san) -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1) `
        -FriendlyName "FocusTime LAN test $ip"
}
$certificateFile = Join-Path $bundle 'FocusTime-LAN.cer'
Export-Certificate -Cert $cert -FilePath $certificateFile -Force | Out-Null
if (-not (Test-Path -LiteralPath ('Cert:\CurrentUser\Root\' + $cert.Thumbprint))) {
    Import-Certificate -FilePath $certificateFile -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
}
$settings = @{
    Address = $ip; AllowedSubnet = $subnet; InterfaceAlias = $InterfaceAlias; HttpsPort = $HttpsPort
    CertificateSubject = $subject; CertificateThumbprint = $cert.Thumbprint; DatabasePort = $DatabasePort
}
$settings | ConvertTo-Json | Set-Content -LiteralPath $lanFile -Encoding UTF8
$hba = Get-Content -LiteralPath $hbaFile -Raw
$backup = Join-Path $runtime 'pg_hba.before-lan.conf'
if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $hbaFile -Destination $backup }
$hba = [regex]::Replace($hba, '(?ms)^# BEGIN FOCUSTIME LAN TEST\r?\n.*?^# END FOCUSTIME LAN TEST\r?\n?', '')
$hba = $hba.TrimEnd() + "`r`n`r`n# BEGIN FOCUSTIME LAN TEST`r`nhost postgres postgres $subnet scram-sha-256`r`n# END FOCUSTIME LAN TEST`r`n"
[IO.File]::WriteAllText($hbaFile, $hba, [Text.UTF8Encoding]::new($false))

$firewallRules = @(
    @{ Name = 'FocusTime-LAN-HTTPS'; Port = $HttpsPort; Program = Join-Path $serverRoot '.tools/dotnet/dotnet.exe' },
    @{ Name = 'FocusTime-LAN-PostgreSQL'; Port = $DatabasePort; Program = Join-Path $serverRoot '.tools/pgsql/bin/postgres.exe' }
)
foreach ($rule in $firewallRules) {
    $arguments = @{
        Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'; Profile = 'Any'; Protocol = 'TCP'
        LocalPort = $rule.Port; LocalAddress = $ip; RemoteAddress = $subnet; InterfaceAlias = $InterfaceAlias
        Program = [IO.Path]::GetFullPath($rule.Program)
    }
    if (Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue) {
        Set-NetFirewallRule -Name $rule.Name @arguments | Out-Null
    } else {
        New-NetFirewallRule -Name $rule.Name -DisplayName $rule.Name @arguments | Out-Null
    }
}

@{
    ApiBaseUrl = $apiUrl; ServerAddress = $ip; DatabasePort = $DatabasePort; AllowedSubnet = $subnet
    CertificateThumbprint = $cert.Thumbprint; CertificateExpires = $cert.NotAfter.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'server-info.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-LanClient.ps1') -Destination $bundle -Force
@"
FocusTime LAN client setup
Server API: $apiUrl
Certificate SHA-1 thumbprint: $($cert.Thumbprint)
1. Copy this entire folder to computer B.
2. In this folder, run: powershell -NoProfile -ExecutionPolicy Bypass -File .\Configure-LanClient.ps1
3. Close and reopen the client/launcher. For a source checkout, run scripts/Start-FocusApp.ps1 from that PowerShell.
The package contains only a public certificate and public connection settings, never a server private key.
The client uses HTTPS API only. Direct PostgreSQL access is optional for database inspection.
"@ | Set-Content -LiteralPath (Join-Path $bundle 'README.txt') -Encoding UTF8
$zip = Join-Path $serverRoot '.artifacts/FocusTime-LAN-client.zip'
Compress-Archive -LiteralPath @($certificateFile, (Join-Path $bundle 'server-info.json'),
    (Join-Path $bundle 'Configure-LanClient.ps1'), (Join-Path $bundle 'README.txt')) -DestinationPath $zip -Force

& (Join-Path $clientRoot 'scripts/Stop-LocalLoginServer.ps1')
& (Join-Path $clientRoot 'scripts/Start-LocalLoginServer.ps1') -ServerRoot $serverRoot -DatabasePort $DatabasePort
$health = Invoke-RestMethod ($apiUrl + 'health/ready') -TimeoutSec 10
if ($health.status -ne 'healthy') { throw 'LAN HTTPS API is not ready.' }
Write-Output "LAN HTTPS ready: $apiUrl"
Write-Output "LAN PostgreSQL: ${ip}:$DatabasePort; database/user: postgres; allowed subnet: $subnet"
Write-Output "Computer B setup package: $zip"
