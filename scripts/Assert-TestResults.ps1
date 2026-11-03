param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [int]$MinimumTests = 48,
    [int]$MinimumPostgresTests = 33
)

$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($files.Count -eq 0) { throw 'No TRX result files were produced.' }

$total = 0
$passed = 0
$databasePassed = 0
foreach ($file in $files) {
    [xml]$document = Get-Content -LiteralPath $file.FullName -Raw
    $counters = $document.TestRun.ResultSummary.Counters
    if (-not $counters) { throw "Missing test counters: $($file.Name)" }
    $fileTotal = [int]$counters.total
    $filePassed = [int]$counters.passed
    if ($fileTotal -ne $filePassed -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0) {
        throw "Tests failed or were skipped: $($file.Name)"
    }
    $total += $fileTotal
    $passed += $filePassed
    foreach ($result in $document.TestRun.Results.UnitTestResult) {
        if (($result.testName.StartsWith('FocusTime.Api.Tests.PostgresTests.', [StringComparison]::Ordinal) -or
            $result.testName.StartsWith('FocusTime.Api.Tests.AccountTests.', [StringComparison]::Ordinal) -or
            $result.testName.StartsWith('FocusTime.Api.Tests.DeviceSessionTests.', [StringComparison]::Ordinal) -or
            $result.testName.StartsWith('FocusTime.Api.Tests.FocusSyncTests.', [StringComparison]::Ordinal) -or
            $result.testName.StartsWith('FocusTime.Api.Tests.FocusBidirectionalTests.', [StringComparison]::Ordinal)) -and
            $result.outcome -eq 'Passed') {
            $databasePassed++
        }
    }
}

if ($total -lt $MinimumTests -or $databasePassed -lt $MinimumPostgresTests) {
    throw "Insufficient coverage: total=$total, PostgreSQL=$databasePassed."
}
Write-Output "Verified $passed/$total passing tests, including $databasePassed PostgreSQL tests; none skipped."
