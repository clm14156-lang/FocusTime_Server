param(
    [string]$PostgresBinDirectory,
    [string]$DotnetPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PostgresBinDirectory) { $PostgresBinDirectory = Join-Path $projectRoot '.tools/pgsql/bin' }
if (-not $DotnetPath) {
    $localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
}
$PostgresBinDirectory = [IO.Path]::GetFullPath($PostgresBinDirectory)
$previousDotnetRoot = $env:DOTNET_ROOT
$previousPath = $env:PATH
if (Test-Path -LiteralPath $DotnetPath -PathType Leaf) {
    $DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
}
foreach ($tool in @('initdb.exe', 'pg_ctl.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PostgresBinDirectory $tool))) {
        throw "PostgreSQL tool missing: $tool. Supply -PostgresBinDirectory."
    }
}

$testRoot = Join-Path $projectRoot ('.artifacts/postgres-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $testRoot 'data'
$passwordFile = Join-Path $testRoot 'password.txt'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
[IO.File]::WriteAllText($passwordFile, $password, [Text.UTF8Encoding]::new($false))
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$previousConnection = $env:TEST_DATABASE_CONNECTION
$started = $false
Push-Location $projectRoot
try {
    if (Test-Path -LiteralPath $DotnetPath -PathType Leaf) {
        $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($DotnetPath)
        $env:PATH = $env:DOTNET_ROOT + ';' + $previousPath
    }
    & (Join-Path $PostgresBinDirectory 'initdb.exe') -D $data -U postgres --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile=$passwordFile
    if ($LASTEXITCODE -ne 0) { throw 'Temporary PostgreSQL initialization failed.' }
    Remove-Item -LiteralPath $passwordFile
    & (Join-Path $PostgresBinDirectory 'pg_ctl.exe') -D $data -l (Join-Path $testRoot 'postgres.log') -o "-h 127.0.0.1 -p $port" -w start
    if ($LASTEXITCODE -ne 0) { throw 'Temporary PostgreSQL startup failed.' }
    $started = $true
    $env:TEST_DATABASE_CONNECTION = "Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Password=$password"
    $testArguments = @('test', '-c', $Configuration, '--logger', 'trx;LogFileName=foundation.trx', '--results-directory', (Join-Path $testRoot 'results'))
    if ($ArtifactsDirectory) { $testArguments += @('--artifacts-path', [IO.Path]::GetFullPath((Join-Path $projectRoot $ArtifactsDirectory))) }
    & $DotnetPath @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Backend foundation tests failed.' }
    Write-Output "Tests completed. Results: $testRoot"
}
finally {
    $env:TEST_DATABASE_CONNECTION = $previousConnection
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:PATH = $previousPath
    if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile }
    if ($started -or (Test-Path -LiteralPath (Join-Path $data 'postmaster.pid'))) {
        & (Join-Path $PostgresBinDirectory 'pg_ctl.exe') -D $data -m fast -w stop
    }
    Pop-Location
}
