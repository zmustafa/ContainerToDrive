#requires -Version 7.4
[CmdletBinding()]
param([string] $ZipRelativePath, [switch] $RequireSigned)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Dependencies.ps1')
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
Invoke-EntryPoint {
    $payload = Get-WorkspacePath 'artifacts/publish/ContainerToDrive'
    Assert-PackagePayload $payload -RequireSigned:$RequireSigned
    if ($ZipRelativePath) { Assert-PackageZip $payload (Get-WorkspacePath $ZipRelativePath) }
    Write-Host 'Payload file set, hashes, engine trust, notices, and requested signatures verified. This is not a mounted/installer test or a complete license/security audit.'
}