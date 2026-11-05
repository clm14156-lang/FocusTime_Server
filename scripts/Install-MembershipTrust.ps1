#requires -Version 5.1
param([Parameter(Mandatory)][string]$TrustFile)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an administrator PowerShell window.'
}
$source = [IO.Path]::GetFullPath($TrustFile)
$config = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
if (-not $config.keys -or $config.keys.Count -gt 128) { throw 'Invalid trust file.' }
foreach ($key in $config.keys) {
    if ($key.apiScope -cnotmatch '^[a-f0-9]{64}$' -or $key.algorithm -ne 'RS256') { throw 'Invalid key scope or algorithm.' }
    $bytes = [Convert]::FromBase64String($key.subjectPublicKeyInfo)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($key.keyId -cne $fingerprint -or $bytes.Length -lt 270 -or $bytes.Length -gt 2048) { throw 'Key fingerprint or public key size mismatch.' }
    # The service imports SPKI, enforces RSA >= 2048 and verifies every signature.
}
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'FocusTime.ServerTrust'
$target = Join-Path $root 'entitlement-trust.json'
foreach ($path in @($root,$target)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Trust paths must not be reparse points.' }
}
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
New-Item -ItemType Directory -Force -Path $root | Out-Null
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($admins); $acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @($admins,$system)) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
Set-Acl -LiteralPath $root -AclObject $acl
# Atomic replacement, retaining read-only access for ordinary users.
$temporary = Join-Path $root ([Guid]::NewGuid().ToString('N')+'.tmp')
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding utf8
$fileAcl = [Security.AccessControl.FileSecurity]::new(); $fileAcl.SetOwner($admins); $fileAcl.SetAccessRuleProtection($true,$false)
foreach ($sid in @($admins,$system)) { $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow')) }
$fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','Allow'))
Set-Acl -LiteralPath $temporary -AclObject $fileAcl
Move-Item -LiteralPath $temporary -Destination $target -Force
Write-Output "Membership public keys installed: $target"
