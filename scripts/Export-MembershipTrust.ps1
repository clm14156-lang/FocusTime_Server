#requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$ServerBaseUrl,
    [string[]]$AllowedBaseUrls,
    [string]$OutputPath = '.artifacts/entitlement-trust.json'
)
$ErrorActionPreference = 'Stop'
function Normalize-BaseUrl([string]$value) {
    $uri = [Uri]$value
    if (-not $uri.IsAbsoluteUri -or ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback)) -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Use HTTPS or loopback HTTP without credentials/query/fragment.' }
    if (-not $uri.AbsoluteUri.EndsWith('/')) { throw 'Base URL must end with /, exactly as configured in the client.' }
    return $uri
}
$server = Normalize-BaseUrl $ServerBaseUrl
$public = Invoke-RestMethod -Uri ([Uri]::new($server, 'api/v1/entitlement-keys'))
if ($public.issuer -ne 'FocusTime_Server' -or $public.keys.Count -lt 1) { throw 'Invalid public key response.' }
if (-not $AllowedBaseUrls) { $AllowedBaseUrls = @($ServerBaseUrl) }
$keys = foreach ($base in $AllowedBaseUrls) {
    $uri = Normalize-BaseUrl $base
    $scope = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uri.AbsoluteUri))).ToLowerInvariant()
    foreach ($key in $public.keys) {
        if ($key.algorithm -ne 'RS256') { throw 'Unsupported signing algorithm.' }
        $bytes = [Convert]::FromBase64String($key.subjectPublicKeyInfo)
        $kid = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($kid -ne $key.keyId) { throw 'Public key fingerprint mismatch.' }
        $rsa = [Security.Cryptography.RSA]::Create()
        try { $read = 0; $rsa.ImportSubjectPublicKeyInfo($bytes, [ref]$read); if ($read -ne $bytes.Length -or $rsa.KeySize -lt 2048) { throw 'Invalid public key.' } }
        finally { $rsa.Dispose() }
        @{ apiScope=$scope; keyId=$kid; algorithm='RS256'; subjectPublicKeyInfo=$key.subjectPublicKeyInfo }
    }
}
$target = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
@{ keys=@($keys) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $target -Encoding utf8
Write-Output "Public trust file exported: $target"
Write-Output ('Key fingerprint: ' + ($public.keys.keyId -join ', '))
