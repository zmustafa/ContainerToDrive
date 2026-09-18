using System.Security.AccessControl;
using System.Security.Cryptography;
using ContainerToDrive.Core;

namespace ContainerToDrive.Windows;

public static class DataRelocation
{
    public static async Task<string> RelocateAsync(ProfileStore source, string destination, CancellationToken token)
    {
        if (AppPaths.IsElevated) throw new InvalidOperationException("Change the data location from a standard-user session.");
        var target = await CopyAsync(source, destination, token);
        token.ThrowIfCancellationRequested();
        DataLocation.Remember(source.DataRoot, target);
        return target;
    }

    public static bool CanRelocate(AppSnapshot? snapshot) => snapshot is not null && !snapshot.Elevated && snapshot.EngineWorkerCount == 0 &&
        snapshot.Mounts.All(mount => mount.Phase == MountPhase.Unmounted && !mount.RecoveryRequired) &&
        snapshot.Profiles.All(profile => snapshot.Mounts.Any(mount => mount.ProfileId == profile.Id && mount.Phase == MountPhase.Unmounted && !mount.RecoveryRequired));

    public static async Task<string> CopyAsync(ProfileStore source, string destination, CancellationToken token)
    {
        destination = DataLocation.ValidateDestination(source.DataRoot, destination);
        token.ThrowIfCancellationRequested();
        var profiles = source.List();
        if (profiles.Any(profile => source.ReadIntent(profile.Id)?.Uncertain == true))
            throw new InvalidOperationException("Resolve recovery before changing the data location.");
        var locks = new List<FileStream>();
        try
        {
            var runtime = Path.Combine(source.DataRoot, "runtime");
            var ownLock = "controller-" + AppPaths.SessionKey(source.DataRoot) + ".lock";
            foreach (var path in Directory.EnumerateFiles(runtime, "controller-*.lock"))
            {
                AppPaths.RejectReparsePoints(path);
                if (Path.GetFileName(path) != ownLock) locks.Add(new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            locks.Add(new(Path.Combine(source.DataRoot, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            var files = Files(source.DataRoot).ToArray();
            var requiredBytes = checked(files.Sum(path => new FileInfo(path).Length) + 16L * 1024 * 1024);
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < requiredBytes)
                throw new IOException("The selected drive does not have enough free space for a verified copy.");
            AppPaths.SecureDirectory(destination);
            var sourceSettings = new ProtectedSettingsStore(source.DataRoot);
            var targetSettings = new ProtectedSettingsStore(destination);
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source.DataRoot, path);
                if (relative.StartsWith("settings\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Path.GetDirectoryName(relative), "settings", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Close other app windows and retry after settings writes finish.");
                    var name = Path.GetFileNameWithoutExtension(path);
                    var bytes = sourceSettings.Read(name) ?? throw new IOException("A setting changed during the copy.");
                    try
                    {
                        targetSettings.Write(name, bytes);
                        var verified = targetSettings.Read(name) ?? throw new IOException("A copied setting could not be verified.");
                        try { if (!bytes.AsSpan().SequenceEqual(verified)) throw new IOException("A copied setting could not be verified."); }
                        finally { CryptographicOperations.ZeroMemory(verified); }
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                else await CopyFileAsync(path, Path.Combine(destination, relative), token);
            }
            var target = new ProfileStore(destination);
            if (!profiles.SequenceEqual(target.List())) throw new IOException("Saved connections changed during the copy.");
            foreach (var profile in profiles)
            {
                _ = target.ReadCredential(profile.Id);
                if (target.ReadIntent(profile.Id)?.Uncertain == true) throw new IOException("Copied connections require recovery.");
            }
            token.ThrowIfCancellationRequested();
            return destination;
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }

    private static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            AppPaths.RejectReparsePoints(entry);
            if (string.Equals(Path.GetFileName(entry), "runtime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(entry), "store.lock", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry))
            {
                foreach (var file in NestedFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    private static IEnumerable<string> NestedFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            AppPaths.RejectReparsePoints(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in NestedFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        AppPaths.RejectReparsePoints(source);
        AppPaths.SecureDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            await using (var output = new FileInfo(destination).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough, AppPaths.FileAcl()))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            await using var copied = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var copiedHash = await SHA256.HashDataAsync(copied, token);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), copiedHash))
                throw new IOException("A copied application file failed verification.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}