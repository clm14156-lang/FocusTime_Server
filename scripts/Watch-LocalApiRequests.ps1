#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet(5080, 5082)][int]$Port = 5082,
    [string]$LogPath,
    [ValidateRange(0, 10000)][int]$Tail = 20,
    [switch]$Snapshot
)

$ErrorActionPreference = 'Stop'
if (-not $LogPath) {
    $serverRoot = Split-Path -Parent $PSScriptRoot
    $adminRoot = Join-Path (Split-Path -Parent $serverRoot) 'focusServe_admin'
    $candidates = @(Join-Path $serverRoot ".artifacts\local-services\api-$Port.log")
    if ($Port -eq 5082) {
        $candidates += Join-Path $adminRoot '.run.local\one-click\logs\backend.log'
    } else {
        $settingsFile = Join-Path $adminRoot '.run.local\one-click\settings.json'
        if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
            $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            $candidates += Join-Path (Split-Path -Parent $settings.DatabaseDataDirectory) 'api.log'
        }
    }
    $log = Get-Item -LiteralPath $candidates -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $log) { throw "No API log found for port $Port. Start the API or specify -LogPath." }
    $LogPath = $log.FullName
}

$LogPath = (Get-Item -LiteralPath $LogPath).FullName
Write-Host "FocusTime API [$Port] - $LogPath"
if (-not $Snapshot) { Write-Host 'Press Ctrl+C to stop watching.' }

Get-Content -LiteralPath $LogPath -Encoding UTF8 -Tail $Tail -Wait:(-not $Snapshot) | ForEach-Object {
    try {
        $entry = ConvertFrom-Json -InputObject $_ -ErrorAction Stop
        if ($entry.Category -ne 'FocusTime.Api.Common.RequestTraceMiddleware' -or
            $null -eq $entry.State.StatusCode) { return }
        $state = $entry.State
        $time = ([DateTimeOffset]$entry.Timestamp).ToLocalTime().ToString('HH:mm:ss.fff')
        $status = [int]$state.StatusCode
        $elapsed = ([double]$state.ElapsedMilliseconds).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
    } catch {
        return
    }
    # The application trace ID is in its own scope; ASP.NET also logs an activity TraceId.
    $trace = $entry.Scopes | Where-Object { $_.TraceId -and -not $_.SpanId } |
        Select-Object -Last 1 -ExpandProperty TraceId
    if ($trace -notmatch '^[0-9a-f]{32}$') { $trace = '-' }
    $method = [regex]::Replace([string]$state.Method, '[\p{Cc}\p{Cf}]', '?')
    $path = [regex]::Replace([string]$state.Path, '[\p{Cc}\p{Cf}]', '?')
    $color = if ($status -ge 500) { 'Red' } elseif ($status -ge 400) { 'Yellow' } else { 'Green' }
    Write-Host ('{0}  [{1}]  {2,-6} {3,-36} {4,3}  {5,7} ms  trace={6}' -f
        $time, $Port, $method, $path, $status, $elapsed, $trace) -ForegroundColor $color
}
