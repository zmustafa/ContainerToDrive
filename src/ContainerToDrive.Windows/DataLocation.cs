using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ContainerToDrive.Windows;

public static class DataLocation
{
    private const string SettingsKey = @"Software\ContainerToDrive\DataLocations";

    public static string Resolve(string source)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return Resolve(source, key);
    }

    internal static string Resolve(string source, RegistryKey? key)
    {
        var root = AppPaths.NormalizeDataRoot(source);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 16; depth++)
        {
            if (!visited.Add(root)) throw new IOException("The saved application location contains a redirect loop.");
            var name = ValueName(root);
            var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return root;
            if (value is not string destination || key!.GetValueKind(name) != RegistryValueKind.String || !Path.IsPathFullyQualified(destination))
                throw new IOException("The saved application location is invalid.");
            root = AppPaths.NormalizeDataRoot(destination);
            AppPaths.RejectReparsePoints(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected application data folder is unavailable.");
        }
        throw new IOException("Too many application data location redirects.");
    }

    public static string ValidateDestination(string source, string destination)
    {
        ValidateSeparatePaths(source, destination);
        destination = AppPaths.NormalizeDataRoot(destination);
        AppPaths.RejectReparsePoints(destination);
        var volume = new DriveInfo(Path.GetPathRoot(destination)!);
        if (volume.DriveType != DriveType.Fixed || !string.Equals(volume.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a folder on a fixed local NTFS drive.");
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty folder dedicated to ContainerToDrive.");
        return destination;
    }

    internal static void ValidateSeparatePaths(string source, string destination)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Application data locations must be absolute paths.");
        source = AppPaths.NormalizeDataRoot(source);
        destination = AppPaths.NormalizeDataRoot(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + "\\", StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The new folder must be separate from the current application data folder.");
    }

    internal static void Remember(string source, string destination)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
        Remember(source, destination, key);
    }

    internal static void Remember(string source, string destination, RegistryKey key)
    {
        ValidateSeparatePaths(source, destination);
        source = AppPaths.NormalizeDataRoot(source);
        destination = AppPaths.NormalizeDataRoot(destination);
        if (!string.Equals(Resolve(source, key), source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Resolve(destination, key), destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An application location changed. Reopen the app before trying again.");
        key.SetValue(ValueName(source), destination, RegistryValueKind.String);
    }

    private static string ValueName(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.NormalizeDataRoot(root).ToUpperInvariant())));
}