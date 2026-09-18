using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Rclone;

public static class EngineLocator
{
    public const string Version = "1.75.1";
    public const string ArchiveHash = "200eb602c126d82aa38b51e0f6b9ae837473ff99b51278d3f6f837574c494d6e";

    public static string FindVerified()
    {
        try { return FindBundledVerified(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return DependencySetup.FindInstalledEngine(Version, ArchiveHash); }
    }

    private static string FindBundledVerified()
    {
        var packageRoot = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(packageRoot, "dependencies.json")) && new DirectoryInfo(packageRoot).Name == "controller")
            packageRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(packageRoot))!.FullName;
        var packageManifest = Path.Combine(packageRoot, "dependencies.json");
        if (File.Exists(packageManifest))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageManifest));
            var rc = doc.RootElement.GetProperty("rclone");
            if (rc.GetProperty("version").GetString() != Version || rc.GetProperty("path").GetString() != "tools/rclone.exe")
                throw new InvalidOperationException("Unsupported packaged engine manifest.");
            var path = Path.Combine(packageRoot, "tools", "rclone.exe");
            Verify(path, rc.GetProperty("sha256").GetString()!);
            return path;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ContainerToDrive.slnx"))) continue;
            var archive = Path.Combine(dir.FullName, ".tools", "downloads", "rclone-v1.75.1-windows-amd64.zip");
            Verify(archive, ArchiveHash);
            using var zip = ZipFile.OpenRead(archive);
            using var content = (zip.GetEntry("rclone-v1.75.1-windows-amd64/rclone.exe") ?? throw new InvalidDataException("Missing pinned engine.")).Open();
            var expected = Convert.ToHexString(SHA256.HashData(content));
            var path = Path.Combine(dir.FullName, ".tools", "rclone", Version, "rclone.exe");
            Verify(path, expected);
            return path;
        }
        throw new FileNotFoundException("Run the local Bootstrap script to obtain the verified rclone engine.");
    }

    public static async Task<List<CapabilityCheck>> CheckCapabilitiesAsync(CancellationToken token)
    {
        var executable = FindVerified();
        var version = await ProbeAsync(executable, ["version"], token);
        if (version.Split('\n')[0].TrimEnd('\r') != "rclone v" + Version)
            throw new InvalidDataException("Unexpected engine version.");
        var mountOutput = await ProbeAsync(executable, ["rc", "--loopback", "mount/types"], token);
        using var mounts = JsonDocument.Parse(mountOutput);
        var supported = mounts.RootElement.GetProperty("mountTypes").EnumerateArray()
            .Any(value => value.GetString() is "mount" or "cmount");
        return
        [
            new("Engine", true, $"Verified rclone {Version}; executable started successfully."),
            new("Mount support", supported, supported ? "Windows mount implementation available." : "No supported Windows mount implementation.")
        ];
    }

    private static async Task<string> ProbeAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--config", "NUL", "--ask-password=false" }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var job = new WorkerJob();
        using var process = Process.Start(start) ?? throw new IOException("The engine could not start.");
        try
        {
            job.Assign(process);
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (process.ExitCode != 0) throw new IOException("The engine capability probe failed.");
            return await output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static void Verify(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Engine integrity verification failed. Restore the approved package.");
    }
}