#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string] $Version = '0.1.0',
    [ValidatePattern('^(unrecorded|[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$')][string] $SourceRevision = 'unrecorded',
    [switch] $Locked,
    [switch] $Msi,
    [switch] $Sign,
    [string] $CertificateThumbprint,
    [string] $SignToolPath,
    [uri] $TimestampUrl
)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Dependencies.ps1')
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
Invoke-EntryPoint {
    $numericVersion = [version]$Version
    if ($numericVersion.Major -gt 255 -or $numericVersion.Minor -gt 255 -or $numericVersion.Build -gt 65535) { throw 'Version exceeds MSI numeric version limits.' }
        if (@(Get-Process -Name 'ContainerToDrive.Desktop', 'ContainerToDrive.Controller',
            'BlobToDrive.Desktop', 'BlobToDrive.Controller' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Close ContainerToDrive through its disconnect workflow before replacing local publish output.'
    }
    if (-not $Sign -and ($CertificateThumbprint -or $SignToolPath -or $TimestampUrl)) { throw 'Signing inputs require the explicit -Sign switch.' }
    if ($Sign) {
        if ($CertificateThumbprint -notmatch '^[a-fA-F0-9]{40}$' -or -not $SignToolPath -or
            -not [IO.Path]::IsPathFullyQualified($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
            $null -eq $TimestampUrl -or -not $TimestampUrl.IsAbsoluteUri -or $TimestampUrl.Scheme -cne 'https' -or
            $TimestampUrl.UserInfo -or $TimestampUrl.Query -or $TimestampUrl.Fragment -or $SourceRevision -eq 'unrecorded') {
            throw 'Signing requires a public certificate thumbprint, absolute external SignTool path, HTTPS timestamp service without credentials/query, and recorded source revision.'
        }
        $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint) -ErrorAction SilentlyContinue
        if ($null -eq $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
            throw 'The selected CurrentUser/My signing certificate/private key is unavailable or outside its validity period.'
        }
    }
    $manifest = Get-DependencyManifest
    $engine = Get-VerifiedRclone $manifest
    Assert-FileHash (Get-WorkspacePath '.tools/winfsp/2.1.25156/License.txt') $manifest.winfsp.licenseSha256
    $dotnet = Get-DotNet
    foreach ($projectName in @('ContainerToDrive.Desktop', 'ContainerToDrive.Controller')) {
        if (-not (Test-Path -LiteralPath (Get-WorkspacePath ("src/$projectName/$projectName.csproj")))) { throw 'Desktop and Controller projects must exist before packaging.' }
    }
    if ($Msi) {
        if ($manifest.wix.version -cne '6.0.2') { throw 'Review the WiX candidate pin before changing installer tooling.' }
        $wix = Get-WorkspacePath $manifest.wix.toolRelativePath
        $wixVersion = Invoke-CheckedProcess $wix @('--version') -Label 'WiX version check' -Capture
        if ($wixVersion -notmatch '^6\.0\.2(?:\+[^\r\n]+)?$') { throw 'The local WiX tool is not the pinned 6.0.2 candidate.' }
    }
    $status = if ($Sign) { 'AUTHENTICODE-SIGNED-APP' } else { 'UNSIGNED' }
    $packageRoot = New-WorkspaceDirectory 'artifacts/packages'
    $baseName = "ContainerToDrive-$Version-win-x64-$status"
    $zipPath = Assert-WorkspacePath (Join-Path $packageRoot ($baseName + '.zip'))
    $msiPath = Assert-WorkspacePath (Join-Path $packageRoot ($baseName + '.msi'))
    if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $msiPath)) { throw 'Package output already exists. Preserve it elsewhere in artifacts or explicitly clean before rebuilding the same version.' }
    Remove-BuildDirectory 'artifacts/publish'
    Remove-BuildDirectory 'artifacts/staging'
    $payload = New-WorkspaceDirectory 'artifacts/publish/ContainerToDrive'
    $properties = @(Get-BuildProperties) + @('-p:SelfContained=true', '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false', '-p:PublishAot=false', '-p:UseAppHost=true',
        '-p:DebugSymbols=false', '-p:DebugType=None', ('-p:Version=' + $Version), ('-p:Configuration=' + $Configuration))
    foreach ($projectName in @('ContainerToDrive.Desktop', 'ContainerToDrive.Controller')) {
        $project = Get-WorkspacePath ("src/$projectName/$projectName.csproj")
        $publish = New-WorkspaceDirectory ("artifacts/staging/$projectName")
        $restore = @('restore', $project, '--runtime', 'win-x64') + $properties
        if ($Locked) { $restore += '--locked-mode' }
        Invoke-CheckedProcess $dotnet $restore -Label "$projectName publish restore"
        Invoke-CheckedProcess $dotnet (@('publish', $project, '--no-restore', '--configuration', $Configuration,
            '--runtime', 'win-x64', '--self-contained', 'true', '--output', $publish) + $properties) -Label "$projectName publish"
        # WPF and console runtime packs have same-name assemblies with different
        # contents (notably WindowsBase). Keep each self-contained app isolated.
        $destination = if ($projectName -eq 'ContainerToDrive.Controller') { New-WorkspaceDirectory 'artifacts/publish/ContainerToDrive/controller' } else { $payload }
        Merge-PublishTree $publish $destination
    }
    Copy-PayloadFile $engine.Path (Get-PayloadPath $payload 'tools/rclone.exe')
    Copy-PayloadFile (Get-WorkspacePath 'installer/licenses/rclone-COPYING.txt') (Get-PayloadPath $payload 'licenses/rclone/COPYING')
    Copy-PayloadFile (Get-WorkspacePath '.tools/winfsp/2.1.25156/License.txt') (Get-PayloadPath $payload 'licenses/winfsp/License.txt')
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'SECURITY.md')) {
        Copy-PayloadFile (Get-WorkspacePath $name) (Get-PayloadPath $payload $name)
    }
    Copy-PayloadFile (Get-WorkspacePath 'installer/INSTALLATION.md') (Get-PayloadPath $payload 'INSTALLATION.md')
    # This project-owned document moves up one level in the package. Preserve upstream license bytes untouched.
    $installationPath = Get-PayloadPath $payload 'INSTALLATION.md'
    $installationText = [IO.File]::ReadAllText($installationPath).Replace('(../SECURITY.md)', '(SECURITY.md)').Replace('(../THIRD-PARTY-NOTICES.md)', '(THIRD-PARTY-NOTICES.md)')
    [IO.File]::WriteAllText($installationPath, $installationText, [Text.UTF8Encoding]::new($false))
    $libraries = @(Get-PublishedLibraries $payload)
    Copy-RuntimeNotices $payload $libraries
    # Deliberately narrow integration contract. The source owner must align its engine reader with this file.
    Write-JsonFile (Get-PayloadPath $payload 'dependencies.json') ([ordered]@{
        schemaVersion = 1
        rclone = [ordered]@{ version = $engine.Version; path = 'tools/rclone.exe'; sha256 = $engine.Sha256 }
        winfsp = [ordered]@{ version = $manifest.winfsp.version; installation = 'external-shared-prerequisite' }
    })
    if ($Sign) {
        foreach ($file in @(Get-SafeTreeFiles $payload | Where-Object { $_.Name -like 'ContainerToDrive.*' -and $_.Extension -in '.exe', '.dll' })) {
            Add-ProjectSignature $file.FullName $SignToolPath $CertificateThumbprint $TimestampUrl
        }
    }
    $sdk = Invoke-CheckedProcess $dotnet @('--version') -Label '.NET SDK inventory' -Capture
    Write-JsonFile (Get-PayloadPath $payload 'dependency-inventory.json') ([ordered]@{
        schemaVersion = 1
        format = 'ContainerToDrive dependency inventory; not SPDX or CycloneDX'
        product = 'ContainerToDrive'; version = $Version; runtimeIdentifier = 'win-x64'; selfContained = $true
        configuration = $Configuration; sourceRevision = $SourceRevision; sdk = $sdk
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        signing = @{ status = $status; thumbprint = $(if ($Sign) { $CertificateThumbprint.ToUpperInvariant() } else { $null }) }
        rclone = @{ version = $engine.Version; sha256 = $engine.Sha256; archiveSha256 = $engine.ArchiveSha256; sourceUrl = $manifest.rclone.sourceUrl; license = 'MIT' }
        winfsp = @{ version = $manifest.winfsp.version; bundled = $false; sourceUrl = $manifest.winfsp.sourceUrl; license = $manifest.winfsp.license }
        dotnetLibraries = $libraries
        licenseReviewComplete = $false
        outstandingReviews = @('rclone transitive Go dependency notices', 'complete NuGet/transitive license obligations',
            'WinFsp FLOSS exception and UI attribution', 'installer lifecycle and clean-VM evidence')
        testEvidence = 'Not asserted by Package; use separately recorded test results.'
    })
    [IO.File]::WriteAllText((Get-PayloadPath $payload 'PACKAGE-STATUS.txt'),
        "$status`nDeveloper package; not a production release or permission to publish.`nThird-party binaries retain their upstream signatures (or unsigned state).`nIntegration, installer lifecycle, and complete license review remain release gates.`n")
    Write-PayloadHashes $payload
    Assert-PackagePayload $payload -RequireSigned:$Sign
    $temporaryZip = Get-WorkspacePath ('artifacts/staging/' + $baseName + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Assert-PackageZip $payload $temporaryZip
    if ($Msi) {
        Remove-BuildDirectory 'artifacts/installer'
        $installerOutput = New-WorkspaceDirectory 'artifacts/installer'
        $candidateMsi = Assert-WorkspacePath (Join-Path $installerOutput ($baseName + '.msi'))
        Invoke-CheckedProcess $wix @('build', (Get-WorkspacePath 'installer/ContainerToDrive.wxs'), '-arch', 'x64',
            '-d', ('PublishDir=' + $payload), '-d', ('ProductVersion=' + $Version), '-d', ('PackageStatus=' + $status),
            '-intermediateFolder', $installerOutput, '-o', $candidateMsi) -Label 'WiX MSI build'
        if ($Sign) { Add-ProjectSignature $candidateMsi $SignToolPath $CertificateThumbprint $TimestampUrl }
        Copy-PayloadFile $candidateMsi $msiPath
    }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath
    $outputs = @([ordered]@{ path = [IO.Path]::GetFileName($zipPath); sha256 = Get-Sha256 $zipPath })
    if ($Msi) { $outputs += [ordered]@{ path = [IO.Path]::GetFileName($msiPath); sha256 = Get-Sha256 $msiPath } }
    Write-JsonFile (Assert-WorkspacePath (Join-Path $packageRoot ($baseName + '.sha256.json'))) @{ schemaVersion = 1; files = $outputs }
    Write-Host "$status developer ZIP verified locally; no publication or installation occurred."
    if ($Msi) { Write-Host 'MSI built only. Clean-VM install/upgrade/uninstall and active-mount safety have not been certified by this script.' }
}