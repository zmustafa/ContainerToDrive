#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-WorkspacePath {
    param([Parameter(Mandatory)][string] $Path, [switch] $AllowRoot)
    $full = [IO.Path]::GetFullPath($Path)
    $root = $script:RepositoryRoot.TrimEnd('\', '/')
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not ($AllowRoot -and $full.Equals($root, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Path is outside the permitted workspace boundary.'
    }
    # Inspect existing ancestry, including the checkout itself, before any write/delete.
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Reparse points are not permitted in build-output ancestry.'
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
    return $full
}

function Get-WorkspacePath {
    param([Parameter(Mandatory)][string] $Relative)
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
        @($Relative -split '[/\\]' | Where-Object { $_ -in '.', '..' }).Count -gt 0) {
        throw 'Use a repository-relative path without dot segments or alternate streams.'
    }
    return Assert-WorkspacePath (Join-Path $script:RepositoryRoot $Relative)
}

function New-WorkspaceDirectory {
    param([Parameter(Mandatory)][string] $Relative)
    $path = Get-WorkspacePath $Relative
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Get-SafeTreeFiles {
    param([Parameter(Mandatory)][string] $Directory)
    $directoryPath = Assert-WorkspacePath $Directory
    foreach ($item in Get-ChildItem -LiteralPath $directoryPath -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A build tree contains a reparse point; nothing may traverse it.'
        }
        if ($item.PSIsContainer) { Get-SafeTreeFiles $item.FullName }
        else { $item }
    }
}

function Remove-BuildDirectory {
    param([Parameter(Mandatory)][string] $Relative)
    # No user-selected paths; .local, .tools, .packages, and unclassified artifacts are never disposable.
    $allowed = @('artifacts/bin', 'artifacts/obj', 'artifacts/testresults', 'artifacts/publish',
        'artifacts/packages', 'artifacts/staging', 'artifacts/installer')
    if ($Relative -cnotin $allowed) { throw 'This directory is not on the build cleanup allowlist.' }
    $path = Get-WorkspacePath $Relative
    if (Test-Path -LiteralPath $path) {
        if (-not (Get-Item -LiteralPath $path -Force).PSIsContainer) { throw 'Expected a build directory.' }
        # Materialize the complete safe traversal BEFORE performing any deletion.
        $null = @(Get-SafeTreeFiles $path)
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-TrustedHash {
    param([Parameter(Mandatory)][string] $Expected)
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$' -or $Expected -match '^0{64}$') {
        throw 'BLOCKED: replace the dependency SHA-256 placeholder with an independently reviewed digest.'
    }
}

function Assert-FileHash {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Expected)
    Assert-TrustedHash $Expected
    $null = Assert-WorkspacePath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Sha256 $Path) -ne $Expected.ToLowerInvariant()) {
        throw 'Dependency is missing or its SHA-256 does not match the trusted manifest. No fallback is permitted.'
    }
}

function Write-JsonFile {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Value)
    $null = Assert-WorkspacePath $Path
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-DependencyManifest {
    $manifest = Get-Content -LiteralPath (Get-WorkspacePath 'scripts/dependencies.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or $manifest.rclone.version -cne '1.75.1' -or
        $manifest.rclone.url -cne 'https://downloads.rclone.org/v1.75.1/rclone-v1.75.1-windows-amd64.zip' -or
        $manifest.rclone.checksumsUrl -cne 'https://downloads.rclone.org/v1.75.1/SHA256SUMS' -or
        $manifest.winfsp.version -cne '2.1.25156' -or
        $manifest.winfsp.url -cne 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi' -or
        $manifest.winfsp.licenseUrl -cne 'https://raw.githubusercontent.com/winfsp/winfsp/v2.1/License.txt') {
        throw 'Dependency version/source changes require review of both the manifest and bootstrap allowlist.'
    }
    return $manifest
}

function Get-DotNet {
    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { throw 'Install the .NET SDK selected by global.json separately; this script never installs an SDK.' }
    return $command.Source
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string[]] $Arguments,
        [string] $Label = 'Tool',
        [switch] $Capture
    )
    if (-not [IO.Path]::IsPathFullyQualified($Executable) -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw "$Label executable is missing; use an absolute verified tool path."
    }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $script:RepositoryRoot
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $start.RedirectStandardOutput = $Capture.IsPresent
    $start.RedirectStandardError = $Capture.IsPresent
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "$Label could not start." }
        if ($Capture) {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
        }
        $process.WaitForExit()
        if ($Capture) {
            $text = $stdout.GetAwaiter().GetResult()
            $null = $stderr.GetAwaiter().GetResult()
        }
        if ($process.ExitCode -ne 0) {
            $exception = [InvalidOperationException]::new("$Label failed with exit code $($process.ExitCode).")
            $exception.Data['ExitCode'] = $process.ExitCode
            throw $exception
        }
        if ($Capture) { return $text.Trim() }
    }
    finally { $process.Dispose() }
}

function Get-BuildProperties {
    return @('-p:UseArtifactsOutput=true', ('-p:ArtifactsPath=' + (Get-WorkspacePath 'artifacts')),
        ('-p:RestorePackagesPath=' + (Get-WorkspacePath '.packages/nuget')))
}

function Invoke-Workspace {
    param([Parameter(Mandatory)][scriptblock] $Action)
    if (-not $IsWindows) { throw 'This entry point requires Windows and PowerShell 7.4 or newer.' }
    $null = Assert-WorkspacePath $script:RepositoryRoot -AllowRoot
    if (-not (Test-Path -LiteralPath (Join-Path $script:RepositoryRoot 'ContainerToDrive.slnx'))) {
        throw 'Workspace marker is missing; refusing to write or clean.'
    }
    $settings = @{
        NUGET_PACKAGES = New-WorkspaceDirectory '.packages/nuget'
        NUGET_HTTP_CACHE_PATH = New-WorkspaceDirectory '.packages/nuget-http'
        NUGET_PLUGINS_CACHE_PATH = New-WorkspaceDirectory '.packages/nuget-plugins'
        DOTNET_CLI_HOME = New-WorkspaceDirectory '.tools/dotnet-home'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        DOTNET_CLI_USE_MSBUILD_SERVER = '0'
        MSBUILDDISABLENODEREUSE = '1'
        TEMP = New-WorkspaceDirectory '.tmp/build'
        TMP = Get-WorkspacePath '.tmp/build'
    }
    # No cloud credentials, inherited rclone overrides, or automatic cloud test opt-in.
    foreach ($variable in Get-ChildItem Env:) {
        if ($variable.Name -match '^(AZURE_|ARM_|AWS_|RCLONE_|CONTAINERTODRIVE_|BLOBTODRIVE_)' -or
            $variable.Name -in 'GOOGLE_APPLICATION_CREDENTIALS', 'GOOGLE_CLOUD_PROJECT', 'GCLOUD_PROJECT') {
            $settings[$variable.Name] = $null
        }
    }
    $settings['CONTAINERTODRIVE_ALLOW_AZURE_TESTS'] = 'false'
    $previous = @{}
    $lock = $null
    $pushed = $false
    try {
        # Serializes cooperating wrappers, not running applications or arbitrary external builds.
        $lockPath = Get-WorkspacePath '.tmp/build/workspace.lock'
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        foreach ($key in $settings.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
        }
        Push-Location $script:RepositoryRoot
        $pushed = $true
        & $Action
    }
    finally {
        if ($pushed) { Pop-Location }
        foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
        if ($null -ne $lock) { $lock.Dispose() }
    }
}

function Invoke-EntryPoint {
    param([Parameter(Mandatory)][scriptblock] $Action)
    try { Invoke-Workspace $Action }
    catch {
        # Do not print exception objects, environment dumps, HTTP bodies, or tool arguments.
        [Console]::Error.WriteLine($_.Exception.Message)
        if ($_.Exception.Data.Contains('ExitCode')) { exit ([int]$_.Exception.Data['ExitCode']) }
        exit 1
    }
    exit 0
}