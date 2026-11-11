#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet(5080, 5082)][int]$Port = 5082)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalServices.Common.ps1')
$settings = Get-LocalServiceSettings
$api = Get-LocalApiSettings $settings $Port
if (Get-LocalApiProcess $settings $api) {
    Write-Output "FocusTime API is already listening at $($api.BaseUrl)."
    return
}
if (-not (Test-LocalDatabaseRunning $settings)) {
    throw 'Start PostgreSQL first with Start-LocalDatabase.ps1.'
}
foreach ($file in @($settings.Dotnet, $settings.DatabasePasswordFile, $api.Dll,
    (Join-Path $api.ContentRoot 'appsettings.json'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file not found: $file" }
}
if (-not $PSCmdlet.ShouldProcess($api.BaseUrl, 'Start FocusTime API')) { return }

$secure = (Get-Content -LiteralPath $settings.DatabasePasswordFile -Raw).Trim() | ConvertTo-SecureString
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
$connection['Host'] = '127.0.0.1'
$connection['Port'] = $settings.DatabasePort
$connection['Database'] = 'postgres'
$connection['Username'] = 'postgres'
$connection['Password'] = [Net.NetworkCredential]::new('', $secure).Password
$connection['Timeout'] = 3
$connection['Command Timeout'] = 10
$environment = @{
    DOTNET_ROOT = Split-Path -Parent $settings.Dotnet
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_ENVIRONMENT = 'Development'
    ConnectionStrings__Database = $connection.ConnectionString
    Email__Mode = 'Pickup'
    Email__PickupDirectory = $api.MailDirectory
    Authentication__Secret = $null
    AdminOverview__UserIds__0 = $null
}
if ($Port -eq 5082) {
    $admin = Get-Content -LiteralPath $settings.AdministratorFile -Raw | ConvertFrom-Json
    $environment.AdminOverview__UserIds__0 = ([guid]$admin.UserId).ToString()
}

$logRoot = Join-Path $settings.ServerRoot '.artifacts\local-services'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$stdout = Join-Path $logRoot "api-$Port.log"
$stderr = Join-Path $logRoot "api-$Port-error.log"
$arguments = '"{0}" --contentRoot "{1}" --urls "{2}"' -f $api.Dll, $api.ContentRoot, $api.BaseUrl
$previous = @{}
try {
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    $process = Start-Process -FilePath $settings.Dotnet -ArgumentList $arguments -WorkingDirectory $api.ContentRoot `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}

$readyUrl = $api.BaseUrl.TrimEnd('/') + '/health/ready'
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($process.HasExited) { throw "FocusTime API exited. See $stderr." }
        try { $health = Invoke-RestMethod -Uri $readyUrl -TimeoutSec 2 -MaximumRedirection 0 }
        catch { $health = $null }
        if ($health.status -eq 'healthy') { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    $listener = Get-LocalApiProcess $settings $api
    if ($health.status -ne 'healthy' -or $listener.Id -ne $process.Id) {
        throw "FocusTime API did not become ready at $readyUrl. See $stdout and $stderr."
    }

    $started = $process.StartTime.ToUniversalTime().Ticks
    if ($Port -eq 5082) {
        # The local verification-code reader checks this exact process identity.
        $record = @{ Pid = $process.Id; Started = $started; BaseUrl = $api.BaseUrl }
    } else {
        $apiConfig = Get-Content -LiteralPath (Join-Path $api.ContentRoot 'appsettings.json') -Raw | ConvertFrom-Json
        $record = @{
            ApiProcessId = $process.Id
            ApiStartTicks = $started.ToString()
            BaseUrl = $api.BaseUrl
            LanBaseUrl = $apiConfig.Kestrel.Endpoints.LanHttps.Url
            DotnetPath = $settings.Dotnet
            PgBin = Split-Path -Parent $settings.PgCtl
            DatabasePort = $settings.DatabasePort
        }
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath $api.ProcessFile -Encoding UTF8
} catch {
    if (-not $process.HasExited) { Stop-Process -InputObject $process }
    throw
}
Write-Output "FocusTime API is ready: $readyUrl"
