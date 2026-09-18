#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [ValidateSet('Unit', 'LocalIntegration')][string] $Tier = 'Unit',
    [switch] $Locked,
    [switch] $NoBuild
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Invoke-EntryPoint {
    # Explicit opt-in tier: local integration uses synthetic credentials and no Azure/driver operations.
    $dotnet = Get-DotNet
    $project = if ($Tier -eq 'Unit') { Get-WorkspacePath 'tests/ContainerToDrive.Tests/ContainerToDrive.Tests.csproj' } else { Get-WorkspacePath 'tests/ContainerToDrive.IntegrationTests/ContainerToDrive.IntegrationTests.csproj' }
    $properties = @(Get-BuildProperties) + @('-p:Configuration=' + $Configuration)
    $results = New-WorkspaceDirectory ('artifacts/testresults/unit-' + [guid]::NewGuid().ToString('N'))
    if (-not $NoBuild) {
        $restore = @('restore', $project, '--packages', (Get-WorkspacePath '.packages/nuget')) + $properties
        if ($Locked) { $restore += '--locked-mode' }
        Invoke-CheckedProcess $dotnet $restore -Label 'Unit restore'
    }
    elseif ($Locked) { throw '-Locked requires restore; do not combine it with -NoBuild.' }
    $testArguments = @('test', $project, '--configuration', $Configuration, '--no-restore',
        '--filter', ('Category=' + $Tier), '--settings', (Get-WorkspacePath 'tests/Unit.runsettings'),
        '--results-directory', $results, '--logger', 'trx;LogFileName=unit.trx') + $properties
    if ($NoBuild) { $testArguments += '--no-build' }
    Invoke-CheckedProcess $dotnet $testArguments -Label 'Unit tests'
    $trx = Join-Path $results 'unit.trx'
    if (-not (Test-Path -LiteralPath $trx -PathType Leaf)) { throw 'The test runner produced no TRX; this is not a passing run.' }
    [xml]$report = [IO.File]::ReadAllText($trx)
    $counters = $report.TestRun.ResultSummary.Counters
    if ([int]$counters.executed -le 0 -or [int]$counters.failed -ne 0 -or
        [int]$counters.passed -ne [int]$counters.executed -or [int]$counters.notExecuted -ne 0) {
        throw 'Unit tests did not all execute and pass. Inspect the local TRX; zero/skipped tests are not success.'
    }
    Write-Host "Executed and passed $($counters.passed) $Tier cases; no Azure or mounted-filesystem tests were run."
}