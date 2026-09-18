using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace ContainerToDrive.Windows;

public static class DependencySetup
{
    private const long MaxDownloadBytes = 256L * 1024 * 1024;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContainerToDrive", "dependencies");
    public static string EngineVersion => ReadDefinition("rclone").Version;
    public static string WinFspVersion => ReadDefinition("winfsp").Version;

    private sealed record Definition(string Version, Uri Uri, string Hash);

    private static Definition ReadDefinition(string name)
    {
        using var stream = typeof(DependencySetup).Assembly.GetManifestResourceStream("ContainerToDrive.Dependencies.json")
            ?? throw new InvalidDataException("Dependency manifest is missing.");
        using var manifest = JsonDocument.Parse(stream);
        var dependency = manifest.RootElement.GetProperty(name);
        return new(dependency.GetProperty("version").GetString()!, new Uri(dependency.GetProperty("url").GetString()!), dependency.GetProperty("sha256").GetString()!);
    }

    public static string FindInstalledEngine(string expectedVersion, string expectedArchiveHash)
    {
        var definition = ReadDefinition("rclone");
        if (definition.Version != expectedVersion || definition.Hash != expectedArchiveHash)
            throw new InvalidDataException("Unsupported dependency manifest.");
        var archivePath = Path.Combine(Root, "rclone-" + definition.Version + ".zip");
        VerifyFile(archivePath, definition.Hash);
        using var archive = ZipFile.OpenRead(archivePath);
        using var source = EngineEntry(archive, definition.Version).Open();
        var enginePath = Path.Combine(Root, "rclone-" + definition.Version, "rclone.exe");
        VerifyFile(enginePath, Convert.ToHexString(SHA256.HashData(source)));
        return enginePath;
    }

    public static async Task InstallEngineAsync(CancellationToken token)
    {
        var definition = ReadDefinition("rclone");
        var archivePath = await DownloadAsync(definition, "rclone-" + definition.Version + ".zip", token);
        var directory = Path.Combine(Root, "rclone-" + definition.Version);
        AppPaths.SecureDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        var enginePath = Path.Combine(directory, "rclone.exe");
        AppPaths.RejectReparsePoints(enginePath);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            await using (var source = EngineEntry(archive, definition.Version).Open())
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await source.CopyToAsync(target, token);
            File.Move(temporary, enginePath, overwrite: true);
            FindInstalledEngine(definition.Version, definition.Hash);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task<string> PrepareWinFspInstallerAsync(CancellationToken token)
    {
        var definition = ReadDefinition("winfsp");
        return DownloadAsync(definition, "winfsp-" + definition.Version + ".msi", token);
    }

    private static ZipArchiveEntry EngineEntry(ZipArchive archive, string version)
    {
        var entries = archive.Entries.Where(entry => entry.FullName == $"rclone-v{version}-windows-amd64/rclone.exe").ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxDownloadBytes)
            throw new InvalidDataException("The approved archive does not contain the expected engine.");
        return entries[0];
    }

    internal static bool IsApprovedDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.DnsSafeHost is "downloads.rclone.org" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    internal static void VerifyFile(string path, string expected)
    {
        AppPaths.RejectReparsePoints(path);
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dependency integrity verification failed.");
    }

    private static async Task<string> DownloadAsync(Definition definition, string name, CancellationToken token)
    {
        AppPaths.SecureDirectory(Root);
        var destination = Path.Combine(Root, name);
        AppPaths.RejectReparsePoints(destination);
        if (File.Exists(destination))
        {
            try { VerifyFile(destination, definition.Hash); return destination; }
            catch (InvalidDataException) { }
        }
        var temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".download");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var current = definition.Uri;
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                if (!IsApprovedDownloadUri(current)) throw new IOException("Unapproved dependency download origin.");
                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    current = response.Headers.Location is { } location ? new Uri(current, location) : throw new IOException("Missing redirect location.");
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new IOException("The dependency download failed or exceeded its size limit.");
                await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
                    {
                        total += count;
                        if (total > MaxDownloadBytes) throw new IOException("Dependency download exceeded its size limit.");
                        await target.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    }
                    await target.FlushAsync(deadline.Token);
                }
                VerifyFile(temporary, definition.Hash);
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            throw new IOException("Too many dependency redirects.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        { throw new IOException("Verified dependency download failed. Check connectivity and retry; no unverified file was launched."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}