param(
    [Parameter(Mandatory)][string]$ClientProjectRoot,
    [string]$ClientDotnetPath = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$ArtifactsDirectory = '.artifacts/client-stage2-build',
    [string]$TestFilter = 'FullyQualifiedName~CloudAccountLiveTests',
    [int]$ExpectedPassed = 1,
    [string]$ServerApiDll
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clientRoot = [IO.Path]::GetFullPath($ClientProjectRoot)
$pg = Join-Path $projectRoot '.tools/pgsql/bin'
$serverDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$apiDll = if ($ServerApiDll) { [IO.Path]::GetFullPath((Join-Path $projectRoot $ServerApiDll)) } else { Join-Path $projectRoot 'src/FocusTime.Api/bin/Release/net10.0/FocusTime.Api.dll' }
$testRoot = Join-Path $projectRoot ('.artifacts/account-client-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $testRoot 'data'
$mail = Join-Path $testRoot 'mail'
$pwFile = Join-Path $testRoot 'password.txt'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); return $port
}
$databasePort = Get-FreePort
$apiPort = Get-FreePort
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
[IO.File]::WriteAllText($pwFile, $password)
$saved = @{}
foreach($name in @('DOTNET_ROOT','ConnectionStrings__Database','ASPNETCORE_ENVIRONMENT','Email__PickupDirectory',
    'FOCUSTIME_TEST_API','FOCUSTIME_TEST_MAIL')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$started = $false
$api = $null
try {
    & "$pg/initdb.exe" -D $data -U postgres --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile=$pwFile
    if($LASTEXITCODE -ne 0){throw 'Temporary PostgreSQL initialization failed.'}
    Remove-Item -LiteralPath $pwFile
    & "$pg/pg_ctl.exe" -D $data -l "$testRoot/postgres.log" -o "-h 127.0.0.1 -p $databasePort" -w start
    if($LASTEXITCODE -ne 0){throw 'Temporary PostgreSQL startup failed.'}
    $started = $true
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($serverDotnet)
    $env:ConnectionStrings__Database = "Host=127.0.0.1;Port=$databasePort;Database=postgres;Username=postgres;Password=$password"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Email__PickupDirectory = $mail
    & $serverDotnet $apiDll --migrate --contentRoot "$projectRoot/src/FocusTime.Api" *> "$testRoot/migrate.log"
    if($LASTEXITCODE -ne 0){throw 'API migration failed. See the isolated migration log.'}
    $api = Start-Process -FilePath $serverDotnet -ArgumentList @($apiDll,'--contentRoot',"$projectRoot/src/FocusTime.Api",'--urls',"http://127.0.0.1:$apiPort") -WindowStyle Hidden -PassThru -RedirectStandardOutput "$testRoot/api.log" -RedirectStandardError "$testRoot/api-error.log"
    $env:FOCUSTIME_TEST_API = "http://127.0.0.1:$apiPort/"
    $env:FOCUSTIME_TEST_MAIL = $mail
    $ready = $false
    for($attempt = 0; $attempt -lt 50; $attempt++) {
        try { if((Invoke-WebRequest "${env:FOCUSTIME_TEST_API}health/ready" -TimeoutSec 2).StatusCode -eq 200){$ready=$true;break} }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if(-not $ready){throw 'Isolated API did not become ready.'}
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($ClientDotnetPath)
    Push-Location $clientRoot
    try {
        & $ClientDotnetPath test tests/FocusApp.Tests/FocusApp.Tests.csproj --no-build --no-restore --artifacts-path (Join-Path $projectRoot $ArtifactsDirectory) --filter $TestFilter --logger 'trx;LogFileName=client-live.trx' --results-directory "$testRoot/results"
        if($LASTEXITCODE -ne 0){throw 'Real API/client account validation failed.'}
    } finally { Pop-Location }
    [xml]$trx = Get-Content -LiteralPath "$testRoot/results/client-live.trx"
    if([int]$trx.TestRun.ResultSummary.Counters.passed -ne $ExpectedPassed -or [int]$trx.TestRun.ResultSummary.Counters.total -ne $ExpectedPassed -or [int]$trx.TestRun.ResultSummary.Counters.notExecuted -ne 0) {
        throw 'The live account test must execute and pass, with no skips.'
    }
    Write-Output "Account/client validation passed. Evidence: $testRoot"
} finally {
    if($api -and -not $api.HasExited){ Stop-Process -Id $api.Id -ErrorAction SilentlyContinue }
    if($started){ & "$pg/pg_ctl.exe" -D $data -m fast -w stop }
    if(Test-Path -LiteralPath $pwFile){Remove-Item -LiteralPath $pwFile}
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
}
