#requires -Version 7.4
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Dependencies.ps1')
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
Invoke-EntryPoint {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Refusing elevated launch. Open a standard-user PowerShell/VS Code session so Explorer sees the same mounts.'
        }
    }
    finally { $identity.Dispose() }
    $manifest = Get-DependencyManifest
    $variable = $manifest.development.dataRootEnvironmentVariable
    if ($manifest.development.dataRootContractVerified -ne $true -or
        -not $variable -or $variable -notmatch '^[A-Z][A-Z0-9_]{2,80}$') {
        throw 'BLOCKED: source owner must verify the development data-root environment contract in scripts/dependencies.json. No installed profiles were opened.'
    }
    $payload = Get-WorkspacePath 'artifacts/publish/ContainerToDrive'
    Assert-PackagePayload $payload
    $dataRoot = New-WorkspaceDirectory '.local/dev'
    $start = [Diagnostics.ProcessStartInfo]::new((Get-PayloadPath $payload 'ContainerToDrive.Desktop.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $payload
    $start.Environment[$variable] = $dataRoot
    # No SAS, request file, controller CLI assumptions, or arbitrary arguments. Desktop owns controller startup.
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Desktop could not start.' }
    $process.Dispose()
    Write-Host 'Desktop process started with isolated development data. This does not assert a mounted drive or healthy controller.'
}