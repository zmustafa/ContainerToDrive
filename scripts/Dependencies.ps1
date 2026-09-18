#requires -Version 7.4
# Dot-source Common.ps1 first. Downloads never install drivers or execute installers.

function Invoke-PinnedDownload {
    param(
        [Parameter(Mandatory)][uri] $Uri,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string[]] $AllowedHosts,
        [long] $MaxBytes = 268435456
    )
    $null = Assert-WorkspacePath $Destination
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    try {
        $current = $Uri
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($current.Scheme -cne 'https' -or -not $current.IsDefaultPort -or $current.UserInfo -or
                $current.Fragment -or $current.DnsSafeHost -notin $AllowedHosts) {
                throw 'Unapproved dependency download origin or redirect.'
            }
            $response = $client.GetAsync($current, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in 301, 302, 303, 307, 308) {
                    if ($null -eq $response.Headers.Location) { throw 'Missing redirect target.' }
                    $current = [uri]::new($current, $response.Headers.Location)
                    continue
                }
                if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -gt $MaxBytes) {
                    throw 'Dependency download failed or exceeded its size limit.'
                }
                $source = $response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
                $target = $null
                try {
                    $target = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $buffer = [byte[]]::new(81920)
                    $total = 0L
                    while (($count = $source.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $count
                        if ($total -gt $MaxBytes) { throw 'Dependency download exceeded its size limit.' }
                        $target.Write($buffer, 0, $count)
                    }
                    $target.Flush($true)
                }
                finally {
                    if ($null -ne $target) { $target.Dispose() }
                    $source.Dispose()
                }
                return
            }
            finally { $response.Dispose() }
        }
        throw 'Too many dependency redirects.'
    }
    catch {
        # GitHub redirect URLs can contain signed queries. Never surface the raw HTTP exception/body/URL.
        throw 'Pinned dependency download failed. Check connectivity and the approved source; no downloaded file is trusted.'
    }
    finally { $deadline.Dispose(); $client.Dispose() }
}

function Receive-VerifiedDependency {
    param(
        [Parameter(Mandatory)][uri] $Uri,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $Sha256,
        [Parameter(Mandatory)][string[]] $AllowedHosts,
        [long] $MaxBytes = 268435456
    )
    Assert-TrustedHash $Sha256
    $null = Assert-WorkspacePath $Destination
    if (Test-Path -LiteralPath $Destination) { Assert-FileHash $Destination $Sha256; return }
    $downloads = New-WorkspaceDirectory '.tmp/downloads'
    $temporary = Join-Path $downloads ([guid]::NewGuid().ToString('N') + '.part')
    try {
        Invoke-PinnedDownload $Uri $temporary $AllowedHosts -MaxBytes $MaxBytes
        Assert-FileHash $temporary $Sha256
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            $null = Assert-WorkspacePath $temporary
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-RcloneArchiveExeHash {
    param([Parameter(Mandatory)] $Manifest)
    $path = Get-WorkspacePath '.tools/downloads/rclone-v1.75.1-windows-amd64.zip'
    Assert-FileHash $path $Manifest.rclone.sha256
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'rclone-v1.75.1-windows-amd64/rclone.exe' })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 268435456) {
            throw 'The approved archive does not contain exactly the expected bounded rclone executable.'
        }
        $stream = $entries[0].Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
        finally { $hash.Dispose(); $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Get-VerifiedRclone {
    param([Parameter(Mandatory)] $Manifest)
    # Derive the executable digest again from the independently trusted ZIP, not from a writable cached report.
    $exeHash = Get-RcloneArchiveExeHash $Manifest
    $executable = Get-WorkspacePath '.tools/rclone/1.75.1/rclone.exe'
    Assert-FileHash $executable $exeHash
    return @{ Path = $executable; Sha256 = $exeHash; Version = $Manifest.rclone.version; ArchiveSha256 = $Manifest.rclone.sha256 }
}