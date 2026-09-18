#requires -Version 7.4
[CmdletBinding()]
param()
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'scripts/Common.ps1')

function Assert-Guard {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-GuardThrows {
    param([Parameter(Mandatory)][scriptblock] $Action)
    $threw = $false
    try { $null = & $Action } catch { $threw = $true }
    Assert-Guard $threw 'Expected the safety boundary to reject the operation.'
}

function Invoke-GuardCase {
    param([string] $Name, [scriptblock] $Action)
    & $Action
    $script:GuardCount++
    Write-Host "PASS: $Name"
}

Invoke-EntryPoint {
    $script:GuardCount = 0
    Invoke-GuardCase 'All authored PowerShell files parse' {
        $scriptFiles = @(
            @(Get-SafeTreeFiles (Get-WorkspacePath 'scripts'))
            @(Get-SafeTreeFiles (Get-WorkspacePath 'tests/scripts'))
        ) | Where-Object Extension -eq '.ps1'
        Assert-Guard (@($scriptFiles).Count -gt 0) 'No PowerShell files were discovered.'
        foreach ($file in $scriptFiles) {
            $tokens = $null
            $syntaxErrors = $null
            $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$syntaxErrors)
            Assert-Guard (@($syntaxErrors).Count -eq 0) ("PowerShell syntax errors in " + $file.Name)
        }
    }
    $fixture = New-WorkspaceDirectory ('artifacts/testresults/guards-' + [guid]::NewGuid().ToString('N'))
    $originalRoot = $script:RepositoryRoot
    $sandbox = Join-Path $fixture 'checkout'
    $outside = Join-Path $fixture 'outside'
    [void][IO.Directory]::CreateDirectory($sandbox)
    [void][IO.Directory]::CreateDirectory($outside)
    $junction = $null
    try {
        # Test helpers against a synthetic checkout only, never the real user's development data.
        $script:RepositoryRoot = $sandbox
        foreach ($directory in @('.local', '.tools', '.packages', 'artifacts/bin', 'artifacts/obj', 'artifacts/evidence')) {
            $path = New-WorkspaceDirectory $directory
            [IO.File]::WriteAllText((Join-Path $path 'synthetic-sentinel.txt'), 'SYNTHETIC TEST DATA ONLY')
        }
        [IO.File]::WriteAllText((Join-Path $outside 'synthetic-sentinel.txt'), 'DO NOT DELETE')
        Invoke-GuardCase 'Allows a child output path' {
            Assert-Guard ((Get-WorkspacePath 'artifacts/bin') -eq (Join-Path $sandbox 'artifacts/bin')) 'Unexpected root resolution.'
        }
        Invoke-GuardCase 'Rejects traversal' { Assert-GuardThrows { Get-WorkspacePath '../outside' } }
        Invoke-GuardCase 'Rejects internal dot segments' { Assert-GuardThrows { Get-WorkspacePath 'artifacts/../.local' } }
        Invoke-GuardCase 'Rejects rooted relative argument' { Assert-GuardThrows { Get-WorkspacePath $outside } }
        Invoke-GuardCase 'Rejects alternate stream syntax' { Assert-GuardThrows { Get-WorkspacePath 'artifacts/file.txt:stream' } }
        Invoke-GuardCase 'Rejects sibling path prefix tricks' { Assert-GuardThrows { Assert-WorkspacePath ($sandbox + '-other/file.txt') } }
        Invoke-GuardCase 'Rejects the root as an output' { Assert-GuardThrows { Assert-WorkspacePath $sandbox } }
        Invoke-GuardCase 'Allows explicit read-only root check' {
            Assert-Guard ((Assert-WorkspacePath $sandbox -AllowRoot) -eq $sandbox) 'Explicit root check failed.'
        }
        foreach ($forbidden in @('.local', '.tools', '.packages', 'artifacts', 'artifacts/evidence', 'artifacts/bin/..', 'artifacts\bin')) {
            Invoke-GuardCase ("Cleanup rejects non-allowlisted target: " + $forbidden) {
                Assert-GuardThrows { Remove-BuildDirectory $forbidden }
            }
        }
        Invoke-GuardCase 'Cleanup removes only its selected output tree' {
            Remove-BuildDirectory 'artifacts/bin'
            Assert-Guard (-not (Test-Path -LiteralPath (Join-Path $sandbox 'artifacts/bin'))) 'Allowed output was not removed.'
            foreach ($retained in @('.local', '.tools', '.packages', 'artifacts/obj', 'artifacts/evidence')) {
                Assert-Guard (Test-Path -LiteralPath (Join-Path $sandbox "$retained/synthetic-sentinel.txt")) 'Cleanup deleted retained synthetic state.'
            }
        }
        Invoke-GuardCase 'Cleanup of missing allowed output is idempotent' { Remove-BuildDirectory 'artifacts/bin' }
        Invoke-GuardCase 'Placeholder and zero hashes cannot establish trust' {
            Assert-GuardThrows { Assert-TrustedHash 'BLOCKED_REPLACE_WITH_HASH' }
            Assert-GuardThrows { Assert-TrustedHash ('0' * 64) }
        }
        Invoke-GuardCase 'Actual matching hash accepted; mismatch rejected' {
            $sentinel = Get-WorkspacePath '.tools/synthetic-sentinel.txt'
            $hash = Get-Sha256 $sentinel
            Assert-FileHash $sentinel $hash
            $different = if ($hash[0] -eq 'a') { 'b' + $hash.Substring(1) } else { 'a' + $hash.Substring(1) }
            Assert-GuardThrows { Assert-FileHash $sentinel $different }
        }
        Invoke-GuardCase 'Junction traversal and cleanup fail before deletion' {
            $junction = Join-Path $sandbox 'artifacts/obj/junction'
            $null = New-Item -Path $junction -ItemType Junction -Target $outside
            Assert-GuardThrows { Get-WorkspacePath 'artifacts/obj/junction/synthetic-sentinel.txt' }
            Assert-GuardThrows { Remove-BuildDirectory 'artifacts/obj' }
            Assert-Guard (Test-Path -LiteralPath (Join-Path $outside 'synthetic-sentinel.txt')) 'Junction target data was deleted.'
            Assert-Guard (Test-Path -LiteralPath (Join-Path $sandbox 'artifacts/obj/synthetic-sentinel.txt')) 'Deletion began before reparse preflight completed.'
        }
    }
    finally {
        # Delete the junction itself, not its target; leave synthetic evidence under test results.
        $link = Join-Path $sandbox 'artifacts/obj/junction'
        try { if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) } }
        finally { $script:RepositoryRoot = $originalRoot }
    }
    Assert-Guard ($script:GuardCount -gt 0) 'No safety cases executed.'
    Write-Host "Executed $($script:GuardCount) script guard cases. No builds, installs, mounts, or cloud tests were run."
}