function Get-LocalServiceSettings {
    $serverRoot = Split-Path -Parent $PSScriptRoot
    $adminRoot = Join-Path (Split-Path -Parent $serverRoot) 'focusServe_admin'
    $runRoot = Join-Path $adminRoot '.run.local\one-click'
    $settingsFile = Join-Path $runRoot 'settings.json'
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if (-not $settings.DatabaseDataDirectory -or [int]$settings.DatabasePort -ne 55432) {
        throw "Expected the existing PostgreSQL instance on port 55432 in $settingsFile."
    }

    $databaseData = [IO.Path]::GetFullPath($settings.DatabaseDataDirectory)
    [pscustomobject]@{
        ServerRoot = $serverRoot
        ClientLocalRoot = Split-Path -Parent $databaseData
        RunRoot = $runRoot
        ApiContentRoot = $settings.ApiContentRoot
        AdministratorFile = $settings.AdministratorFile
        DatabasePasswordFile = $settings.DatabasePasswordFile
        DatabaseData = $databaseData
        DatabasePort = [int]$settings.DatabasePort
        PgCtl = Join-Path $serverRoot '.tools\pgsql\bin\pg_ctl.exe'
        Dotnet = Join-Path $serverRoot '.tools\dotnet\dotnet.exe'
    }
}

function Test-LocalDatabaseRunning($Settings) {
    if (-not (Test-Path -LiteralPath (Join-Path $Settings.DatabaseData 'PG_VERSION') -PathType Leaf)) {
        throw "Existing PostgreSQL data directory not found: $($Settings.DatabaseData)"
    }
    & $Settings.PgCtl -D $Settings.DatabaseData status *> $null
    switch ($LASTEXITCODE) {
        0 { return $true }
        3 { return $false }
        default { throw "pg_ctl status failed with exit code $LASTEXITCODE." }
    }
}

function Get-LocalApiSettings($Settings, [int]$Port) {
    if ($Port -eq 5082) {
        $root = [IO.Path]::GetFullPath($Settings.ApiContentRoot)
        return [pscustomobject]@{
            Port = $Port
            BaseUrl = 'http://127.0.0.1:5082'
            ContentRoot = $root
            Dll = Join-Path $Settings.RunRoot 'api-build\FocusTime.Api.dll'
            MailDirectory = Join-Path $root 'mail'
            ProcessFile = Join-Path $root 'process.json'
        }
    }

    $root = Join-Path $Settings.ClientLocalRoot 'api'
    [pscustomobject]@{
        Port = $Port
        BaseUrl = 'http://localhost:5080/'
        ContentRoot = $root
        Dll = Join-Path $Settings.ServerRoot 'src\FocusTime.Api\bin\Release\net10.0\FocusTime.Api.dll'
        MailDirectory = Join-Path $root '.artifacts\mail'
        ProcessFile = Join-Path $Settings.ClientLocalRoot 'process.json'
    }
}

function Get-LocalApiProcess($Settings, $Api) {
    $owners = @(Get-NetTCPConnection -State Listen |
        Where-Object LocalPort -eq $Api.Port |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -eq 0) { return }
    if ($owners.Count -ne 1) {
        throw "Multiple processes are listening on port $($Api.Port)."
    }

    $process = Get-Process -Id $owners[0] -ErrorAction SilentlyContinue
    if (-not $process) { return }
    $details = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($process.Id)"
    $expectedDll = [regex]::Escape($Api.Dll)
    if ($details.ExecutablePath -ne $Settings.Dotnet -or
        ([string]$details.CommandLine).Replace('/', '\') -notmatch ('(?:^|[\s"])' + $expectedDll + '(?:[\s"]|$)')) {
        throw "Port $($Api.Port) is occupied by PID $($process.Id), which does not match the configured FocusTime API."
    }
    return $process
}
