#requires -Version 7.4
[CmdletBinding()]
param([switch] $DownloadWinFsp)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Dependencies.ps1')
Invoke-EntryPoint {
    $manifest = Get-DependencyManifest
    # Trust is reviewed in source before networking. A checksum fetched beside a binary is NOT a trust anchor.
    Assert-TrustedHash $manifest.rclone.sha256
    Assert-TrustedHash $manifest.winfsp.licenseSha256
    if ($DownloadWinFsp) { Assert-TrustedHash $manifest.winfsp.sha256 }
    $downloadRoot = New-WorkspaceDirectory '.tools/downloads'
    $rcloneRoot = New-WorkspaceDirectory '.tools/rclone/1.75.1'
    $winfspRoot = New-WorkspaceDirectory '.tools/winfsp/2.1.25156'
    $archivePath = Join-Path $downloadRoot 'rclone-v1.75.1-windows-amd64.zip'
    Receive-VerifiedDependency $manifest.rclone.url $archivePath $manifest.rclone.sha256 @('downloads.rclone.org')

    # Additional published-checksum consistency check, anchored to the already reviewed archive digest.
    $sumsPath = Join-Path (New-WorkspaceDirectory '.tmp/downloads') ([guid]::NewGuid().ToString('N') + '.sums')
    try {
        Invoke-PinnedDownload $manifest.rclone.checksumsUrl $sumsPath @('downloads.rclone.org') -MaxBytes 1048576
        $pattern = '^([a-fA-F0-9]{64})\s+\*?rclone-v1\.75\.1-windows-amd64\.zip\s*$'
        $matching = @([IO.File]::ReadAllLines($sumsPath) | Where-Object { $_ -match $pattern })
        if ($matching.Count -ne 1) { throw 'The published SHA256SUMS entry is missing or ambiguous.' }
        $publishedHash = [regex]::Match($matching[0], $pattern).Groups[1].Value
        if ($publishedHash -ne $manifest.rclone.sha256) { throw 'Published SHA256SUMS disagrees with the reviewed manifest.' }
        [IO.File]::Copy($sumsPath, (Assert-WorkspacePath (Join-Path $downloadRoot 'rclone-v1.75.1-SHA256SUMS')), $true)
    }
    finally {
        if (Test-Path -LiteralPath $sumsPath) {
            $null = Assert-WorkspacePath $sumsPath
            Remove-Item -LiteralPath $sumsPath -Force
        }
    }

    $expectedExeHash = Get-RcloneArchiveExeHash $manifest
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        # Extract only these exact entries to fixed destinations; never Expand-Archive arbitrary paths.
        foreach ($name in @('rclone.exe', 'README.txt', 'README.html', 'rclone.1')) {
            $entry = $archive.GetEntry('rclone-v1.75.1-windows-amd64/' + $name)
            if ($null -eq $entry) {
                if ($name -eq 'rclone.exe') { throw 'The executable is missing from the pinned archive.' }
                continue
            }
            if ($entry.Length -gt 268435456) { throw 'An archive entry exceeds the extraction size limit.' }
            $destination = Assert-WorkspacePath (Join-Path $rcloneRoot $name)
            if ($name -eq 'rclone.exe' -and (Test-Path -LiteralPath $destination)) {
                Assert-FileHash $destination $expectedExeHash
                continue
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
    finally { $archive.Dispose() }
    [IO.File]::Copy((Get-WorkspacePath 'installer/licenses/rclone-COPYING.txt'),
        (Assert-WorkspacePath (Join-Path $rcloneRoot 'COPYING')), $true)
    Receive-VerifiedDependency $manifest.winfsp.licenseUrl (Join-Path $winfspRoot 'License.txt') $manifest.winfsp.licenseSha256 @('raw.githubusercontent.com') -MaxBytes 1048576

    $engine = Get-VerifiedRclone $manifest
    $versionOutput = Invoke-CheckedProcess $engine.Path @('version', '--config', 'NUL') -Label 'Pinned rclone version check' -Capture
    if ($versionOutput -notmatch '(?m)^rclone v1\.75\.1\r?$') { throw 'The verified executable did not report the pinned rclone version.' }
    if ($DownloadWinFsp) {
        $msi = Join-Path $winfspRoot 'winfsp-2.1.25156.msi'
        Receive-VerifiedDependency $manifest.winfsp.url $msi $manifest.winfsp.sha256 @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')
        $signature = Get-AuthenticodeSignature -LiteralPath $msi
        if ($signature.Status -ne 'Valid') { throw 'Official WinFsp MSI Authenticode validation failed; do not install it.' }
        Write-Host 'Official WinFsp MSI downloaded and verified only. Installation/repair is a separate manual administrator operation.'
    }
    Write-JsonFile (Get-WorkspacePath '.tools/dependencies.verified.json') @{
        schemaVersion = 1
        rclone = @{ version = $engine.Version; archiveSha256 = $engine.ArchiveSha256; executableSha256 = $engine.Sha256 }
        winfspLicenseSha256 = $manifest.winfsp.licenseSha256
        winfspMsiDownloadedThisRun = $DownloadWinFsp.IsPresent
    }
    Write-Host 'Pinned tools/licenses are ready. No SDK, driver, WiX tool, cloud resources, or global environment settings were installed.'
}