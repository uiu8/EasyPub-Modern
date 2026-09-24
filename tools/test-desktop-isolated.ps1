param(
    [string]$Dotnet = 'D:\software\dotnet-sdk-10.0.302\dotnet.exe',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'tests/EasyPub.Desktop.Tests/EasyPub.Desktop.Tests.csproj'
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repo ('work/test-results/desktop-isolated-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
& $Dotnet build $project -c Release --no-restore -m:1
if ($LASTEXITCODE -ne 0) { throw 'Desktop test build failed.' }
$listed = & $Dotnet test $project -c Release --no-build --no-restore --list-tests
if ($LASTEXITCODE -ne 0) { throw 'Test discovery failed.' }
$cases = @($listed | Where-Object { $_ -match '^\s+EasyPub\.Desktop\.Tests\.' })
$names = @($listed | ForEach-Object {
    if ($_ -match '^\s+(EasyPub\.Desktop\.Tests\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+)') { $Matches[1] }
} | Sort-Object -Unique)
if ($names.Count -eq 0) { throw 'No desktop tests discovered.' }
# WPF Application is process-global. These resource tests intentionally create
# different application kinds, so each method needs its own process as well.
$filters = @($names | ForEach-Object {
    if ($_ -like '*ControlStyleContractTests.*') { 'FullyQualifiedName=' + $_ }
    else { 'FullyQualifiedName~' + $_.Substring(0, $_.LastIndexOf('.')) + '.' }
} | Sort-Object -Unique)
$failed = @()
for ($index = 0; $index -lt $filters.Count; $index++) {
    $filter = $filters[$index]
    Write-Host "[$($index + 1)/$($filters.Count)] $filter"
    & $Dotnet test $project -c Release --no-build --no-restore --filter $filter --logger "trx;LogFileName=group-$index.trx" --results-directory $ResultsDirectory -m:1 -- xUnit.MaxParallelThreads=1
    if ($LASTEXITCODE -ne 0) { $failed += $filter }
}
$failed | Set-Content (Join-Path $ResultsDirectory 'failed-filters.txt') -Encoding UTF8
$total = 0; $passed = 0
for ($index = 0; $index -lt $filters.Count; $index++) {
    [xml]$report = Get-Content -LiteralPath (Join-Path $ResultsDirectory "group-$index.trx") -Raw
    $total += [int]$report.TestRun.ResultSummary.Counters.total
    $passed += [int]$report.TestRun.ResultSummary.Counters.passed
}
Write-Host "Discovered=$($cases.Count); Executed=$total; Passed=$passed"
if ($total -ne $cases.Count) { throw 'Isolated run did not execute exactly the discovered test count.' }
Write-Host "Reports: $ResultsDirectory"
if ($failed.Count -gt 0) { throw "$($failed.Count) isolated test groups failed. See failed-filters.txt." }
