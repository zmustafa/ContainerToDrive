#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param()
. (Join-Path $PSScriptRoot 'Common.ps1')
# Preserve the entry script's SupportsShouldProcess context across helper-function scopes.
$cleanCmdlet = $PSCmdlet
Invoke-EntryPoint {
    # Refuse a known app process rather than stopping it or deleting its in-use output.
    $active = @(Get-Process -Name 'ContainerToDrive.Desktop', 'ContainerToDrive.Controller',
        'BlobToDrive.Desktop', 'BlobToDrive.Controller' -ErrorAction SilentlyContinue)
    if ($active.Count -gt 0) { throw 'Close ContainerToDrive through its tested disconnect workflow before cleaning; no process was stopped.' }
    foreach ($relative in @('artifacts/bin', 'artifacts/obj', 'artifacts/testresults', 'artifacts/publish',
            'artifacts/packages', 'artifacts/staging', 'artifacts/installer')) {
        if ($cleanCmdlet.ShouldProcess($relative, 'Delete allowlisted build output (not application data)')) {
            Remove-BuildDirectory $relative
        }
    }
    Write-Host 'Cleanup/preview completed. Development/recovery data, dependencies, caches, and other artifacts were preserved.'
}