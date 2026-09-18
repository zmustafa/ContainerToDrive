#requires -Version 7.4
# Dot-source Common.ps1 and Dependencies.ps1 first.

function Get-PayloadPath {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $Relative)
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative.Contains('\') -or
        @($Relative.Split('/') | Where-Object { $_ -in '', '.', '..' }).Count -gt 0) {
        throw 'Package contains an unsafe or noncanonical relative path.'
    }
    $path = Assert-WorkspacePath (Join-Path $Root $Relative)
    if (-not $path.StartsWith($Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package path escaped its payload root.'
    }
    return $path
}

function Copy-PayloadFile {
    param([Parameter(Mandatory)][string] $Source, [Parameter(Mandatory)][string] $Destination)
    $null = Assert-WorkspacePath $Source
    $null = Assert-WorkspacePath $Destination
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    if (Test-Path -LiteralPath $Destination) {
        if ((Get-Sha256 $Source) -ne (Get-Sha256 $Destination)) {
            throw 'Publish outputs disagree on a shared file; refusing to overwrite one application with the other.'
        }
        return
    }
    [IO.File]::Copy($Source, $Destination, $false)
}

function Merge-PublishTree {
    param([Parameter(Mandatory)][string] $Source, [Parameter(Mandatory)][string] $Destination)
    foreach ($file in @(Get-SafeTreeFiles $Source)) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
        Copy-PayloadFile $file.FullName (Get-PayloadPath $Destination $relative)
    }
}

function Get-PublishedLibraries {
    param([Parameter(Mandatory)][string] $Payload)
    $libraries = @{}
    foreach ($file in @(Get-SafeTreeFiles $Payload | Where-Object { $_.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase) })) {
        $deps = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -AsHashtable
        foreach ($entry in $deps.libraries.GetEnumerator()) {
            $parts = $entry.Key -split '/', 2
            if ($parts.Count -ne 2) { throw 'Invalid published dependency identity.' }
            $libraries[$entry.Key] = [ordered]@{ name = $parts[0]; version = $parts[1]; type = $entry.Value.type }
        }
    }
    if ($libraries.Count -eq 0) { throw 'No dependency metadata was produced by self-contained publishing.' }
    return @($libraries.GetEnumerator() | Sort-Object Key | ForEach-Object { $_.Value })
}

function Copy-RuntimeNotices {
    param([Parameter(Mandatory)][string] $Payload, [Parameter(Mandatory)][object[]] $Libraries)
    $runtimePacks = @($Libraries | Where-Object { $_.name -match '^runtimepack\.Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$' })
    if ($runtimePacks.Count -lt 2) { throw 'Expected self-contained .NET and WPF runtime packs; review the publish dependency metadata.' }
    foreach ($pack in $runtimePacks) {
        $packageId = $pack.name.Substring('runtimepack.'.Length).ToLowerInvariant()
        $packageRoot = Get-WorkspacePath ('.packages/nuget/' + $packageId + '/' + $pack.version)
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -File -Force | Where-Object { $_.Name -match '^(LICENSE|THIRD[-_]?PARTY[-_]?NOTICES)(\..+)?$' })
        if (@($notices | Where-Object { $_.Name -match '^LICENSE' }).Count -eq 0 -or
            ($packageId -like '*netcore*' -and @($notices | Where-Object { $_.Name -match '^THIRD' }).Count -eq 0)) {
            throw 'A self-contained runtime pack is missing its full license/third-party notice material.'
        }
        foreach ($notice in $notices) {
            Copy-PayloadFile $notice.FullName (Get-PayloadPath $Payload ('licenses/' + $packageId + '/' + $notice.Name))
        }
        if ($packageId -like '*windowsdesktop*') {
            if ($pack.version -ne '10.0.11') { throw 'Review and pin Windows Desktop source notices for the new runtime version.' }
            $sourceNotices = @{
                wpf = 'd23dba2fee20c6b59d8f7088ee9ed372459c952a319b7ca90f3eec370cc76eb9'
                winforms = '5a412b38efae0b162dc0893b319023f4cd14a8985a0f9f9ae01ac052b37a36eb'
            }
            foreach ($component in $sourceNotices.Keys) {
                $local = Get-WorkspacePath ('.tools/licenses/' + $component + '-10.0.11-THIRD-PARTY-NOTICES.txt')
                Receive-VerifiedDependency ('https://raw.githubusercontent.com/dotnet/' + $component + '/v10.0.11/THIRD-PARTY-NOTICES.TXT') $local $sourceNotices[$component] @('raw.githubusercontent.com') -MaxBytes 1048576
                Copy-PayloadFile $local (Get-PayloadPath $Payload ('licenses/' + $component + '/THIRD-PARTY-NOTICES.txt'))
            }
        }
    }
}

function Assert-SignedFile {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Thumbprint)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $Thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw 'A project-owned file lacks the expected valid, timestamped Authenticode signature.'
    }
}

function Add-ProjectSignature {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $SignToolPath,
        [Parameter(Mandatory)][string] $Thumbprint,
        [Parameter(Mandatory)][uri] $TimestampUrl
    )
    $null = Invoke-CheckedProcess $SignToolPath @('sign', '/sha1', $Thumbprint, '/s', 'My',
        '/fd', 'SHA256', '/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256', $Path) -Label 'Authenticode signing' -Capture
    Assert-SignedFile $Path $Thumbprint
}

function Assert-PayloadPolicy {
    param([Parameter(Mandatory)][string] $Payload)
    foreach ($file in @(Get-SafeTreeFiles $Payload)) {
        $relative = [IO.Path]::GetRelativePath($Payload, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(\.local|\.git|\.tools|\.packages|\.tmp|TestResults|secrets|profiles|cache)(/|$)' -or
            $file.Name -match '(^\.env($|\.)|\.(pfx|p12|pem|key|dpapi|trx|binlog|dmp|pdb)$)') {
            throw 'Forbidden data, signing material, or development output found in the package.'
        }
        # A bounded defense-in-depth text check, not an assertion of complete secret scanning.
        if ($file.Extension -in '.json', '.config', '.xml', '.md', '.txt') {
            if ($file.Length -gt 33554432) { throw 'Package text exceeds the review bound.' }
            $text = [IO.File]::ReadAllText($file.FullName)
            if ($text -match '(?i)https?://[^\s"<>]*[?&]sig=' -or
                $text -match '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
                throw 'Potential credential material found in package text; content was not printed.'
            }
        }
    }
}

function Write-PayloadHashes {
    param([Parameter(Mandatory)][string] $Payload)
    $files = @(Get-SafeTreeFiles $Payload | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Payload, $_.FullName).Replace('\', '/')
        if ($relative -cne 'package-files.json') {
            [ordered]@{ path = $relative; length = $_.Length; sha256 = Get-Sha256 $_.FullName }
        }
    })
    Write-JsonFile (Get-PayloadPath $Payload 'package-files.json') ([ordered]@{
        schemaVersion = 1; hashAlgorithm = 'SHA256'; excludes = @('package-files.json'); files = $files
    })
}

function Assert-PackagePayload {
    param([Parameter(Mandatory)][string] $Payload, [switch] $RequireSigned)
    $null = Assert-WorkspacePath $Payload
    Assert-PayloadPolicy $Payload
    foreach ($required in @('ContainerToDrive.Desktop.exe', 'ContainerToDrive.Desktop.dll', 'ContainerToDrive.Desktop.deps.json',
            'ContainerToDrive.Desktop.runtimeconfig.json', 'controller/ContainerToDrive.Controller.exe', 'controller/ContainerToDrive.Controller.dll',
            'controller/ContainerToDrive.Controller.deps.json', 'controller/ContainerToDrive.Controller.runtimeconfig.json', 'controller/coreclr.dll', 'coreclr.dll',
            'PresentationFramework.dll', 'tools/rclone.exe', 'dependencies.json', 'dependency-inventory.json',
            'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/rclone/COPYING', 'licenses/winfsp/License.txt',
            'INSTALLATION.md', 'PACKAGE-STATUS.txt', 'package-files.json')) {
        if (-not (Test-Path -LiteralPath (Get-PayloadPath $Payload $required) -PathType Leaf)) { throw 'Required package payload is incomplete.' }
    }
    $hashes = [IO.File]::ReadAllText((Get-PayloadPath $Payload 'package-files.json')) | ConvertFrom-Json -AsHashtable
    if ($hashes.schemaVersion -ne 1 -or $hashes.hashAlgorithm -cne 'SHA256') { throw 'Unsupported package hash format.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $hashes.files) {
        $path = Get-PayloadPath $Payload $entry.path
        if ($entry.path -ieq 'package-files.json' -or -not $expected.Add($entry.path)) { throw 'Duplicate or self-referential package hash entry.' }
        Assert-FileHash $path $entry.sha256
        if ((Get-Item -LiteralPath $path).Length -ne $entry.length) { throw 'Package file length mismatch.' }
    }
    $actual = @(Get-SafeTreeFiles $Payload)
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($Payload, $file.FullName).Replace('\', '/')
        if ($relative -cne 'package-files.json' -and -not $expected.Contains($relative)) { throw 'Unlisted extra file found in package.' }
    }
    if ($expected.Count -ne $actual.Count - 1) { throw 'Package file inventory is incomplete.' }
    $manifest = Get-DependencyManifest
    $engine = Get-VerifiedRclone $manifest
    Assert-FileHash (Get-PayloadPath $Payload 'tools/rclone.exe') $engine.Sha256
    Assert-FileHash (Get-PayloadPath $Payload 'licenses/winfsp/License.txt') $manifest.winfsp.licenseSha256
    Assert-FileHash (Get-PayloadPath $Payload 'licenses/rclone/COPYING') (Get-Sha256 (Get-WorkspacePath 'installer/licenses/rclone-COPYING.txt'))
    $runtime = [IO.File]::ReadAllText((Get-PayloadPath $Payload 'dependencies.json')) | ConvertFrom-Json -AsHashtable
    if ($runtime.schemaVersion -ne 1 -or $runtime.rclone.version -cne $engine.Version -or
        $runtime.rclone.path -cne 'tools/rclone.exe' -or $runtime.rclone.sha256 -cne $engine.Sha256) {
        throw 'Runtime engine manifest and trusted payload disagree.'
    }
    $inventory = [IO.File]::ReadAllText((Get-PayloadPath $Payload 'dependency-inventory.json')) | ConvertFrom-Json -AsHashtable
    if ($inventory.signing.status -notin 'UNSIGNED', 'AUTHENTICODE-SIGNED-APP') { throw 'Invalid package signing status.' }
    if ($RequireSigned -and $inventory.signing.status -cne 'AUTHENTICODE-SIGNED-APP') { throw 'An unsigned developer build cannot satisfy signed verification.' }
    if ($inventory.signing.status -ceq 'AUTHENTICODE-SIGNED-APP') {
        if ($inventory.signing.thumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Missing signer identity.' }
        foreach ($file in @($actual | Where-Object { $_.Name -like 'ContainerToDrive.*' -and $_.Extension -in '.exe', '.dll' })) {
            Assert-SignedFile $file.FullName $inventory.signing.thumbprint
        }
    }
    $label = [IO.File]::ReadAllText((Get-PayloadPath $Payload 'PACKAGE-STATUS.txt'))
    if (-not $label.StartsWith($inventory.signing.status, [StringComparison]::Ordinal)) { throw 'Package label and signing inventory disagree.' }
}

function Assert-PackageZip {
    param([Parameter(Mandatory)][string] $Payload, [Parameter(Mandatory)][string] $ZipPath)
    $null = Assert-WorkspacePath $ZipPath
    $files = @{}
    foreach ($file in @(Get-SafeTreeFiles $Payload)) {
        $files[[IO.Path]::GetRelativePath($Payload, $file.FullName).Replace('\', '/')] = $file
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $null = Get-PayloadPath $Payload $entry.FullName
            if (-not $seen.Add($entry.FullName) -or -not $files.ContainsKey($entry.FullName)) { throw 'ZIP has an unexpected or duplicate entry.' }
            if ($entry.Length -ne $files[$entry.FullName].Length) { throw 'ZIP file length differs from staged payload.' }
            $stream = $entry.Open()
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
            finally { $hash.Dispose(); $stream.Dispose() }
            if ($actualHash -ne (Get-Sha256 $files[$entry.FullName].FullName)) { throw 'ZIP bytes differ from staged payload.' }
        }
        if ($seen.Count -ne $files.Count) { throw 'ZIP omits staged payload files.' }
    }
    finally { $archive.Dispose() }
}