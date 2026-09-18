#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $Locked
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Invoke-EntryPoint {
    $dotnet = Get-DotNet
    $solution = Get-WorkspacePath 'ContainerToDrive.slnx'
    $properties = @(Get-BuildProperties) + @('-p:Configuration=' + $Configuration)
    $restore = @('restore', $solution, '--packages', (Get-WorkspacePath '.packages/nuget')) + $properties
    if ($Locked) { $restore += '--locked-mode' }
    Invoke-CheckedProcess $dotnet $restore -Label 'Solution restore'
    Invoke-CheckedProcess $dotnet (@('build', $solution, '--no-restore', '--configuration', $Configuration) + $properties) -Label 'Solution build'
}