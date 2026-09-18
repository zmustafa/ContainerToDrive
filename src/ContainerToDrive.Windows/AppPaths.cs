using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ContainerToDrive.Windows;

public static class AppPaths
{
    public static string CurrentSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.User?.Value ?? throw new UnauthorizedAccessException("The current Windows user could not be verified.");
        }
    }

    public static bool IsElevated => ProcessIdentity.IsCurrentProcessElevated();

    public static string ResolveDataRoot(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? selected = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument != "--data-root" && !argument.StartsWith("--data-root=", StringComparison.Ordinal)) continue;
            if (selected is not null) throw new ArgumentException("Specify the data root only once.");
            if (argument == "--data-root")
            {
                if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("The data-root option requires a directory.");
                selected = args[index];
            }
            else selected = argument["--data-root=".Length..];
            if (string.IsNullOrWhiteSpace(selected)) throw new ArgumentException("The data root cannot be empty.");
        }

        if (selected is null)
        {
            selected = Environment.GetEnvironmentVariable("CONTAINERTODRIVE_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(selected))
                selected = Environment.GetEnvironmentVariable("BLOBTODRIVE_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(selected))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) throw new IOException("Local application data is unavailable.");
                selected = ResolveDefaultDataRoot(local);
            }
        }
        return DataLocation.Resolve(NormalizeDataRoot(selected));
    }

    internal static string ResolveDefaultDataRoot(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var current = NormalizeDataRoot(Path.Combine(localApplicationData, "ContainerToDrive"));
        var legacy = NormalizeDataRoot(Path.Combine(localApplicationData, "BlobToDrive"));
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        RejectReparsePoints(legacy);
        Directory.Move(legacy, current);
        return current;
    }

    public static string SessionKey(string dataRoot)
        => SessionKey(dataRoot, "ContainerToDrive");

    internal static string LegacySessionKey(string dataRoot)
        => SessionKey(dataRoot, "BlobToDrive");

    internal static bool LegacyControllerIsRunning(string dataRoot)
    {
        var root = NormalizeDataRoot(dataRoot);
        var path = Path.Combine(root, "runtime", "controller-" + LegacySessionKey(root) + ".lock");
        if (!FileExists(path)) return false;
        SecureFile(path);
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException exception) when (IsSharingViolation(exception)) { return true; }
    }

    private static string SessionKey(string dataRoot, string productName)
    {
        // Relative spellings, casing, and trailing separators must not create a second controller.
        var root = NormalizeDataRoot(dataRoot).ToUpperInvariant();
        using var process = Process.GetCurrentProcess();
        var identity = $"{productName}\n{CurrentSid}\n{process.SessionId}\n{root}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static string PipeName(string dataRoot) => "ContainerToDrive-" + SessionKey(dataRoot);

    /// <summary>Creates/protects an application-owned local NTFS directory, never an entire volume.</summary>
    public static void SecureDirectory(string path)
    {
        var fullPath = NormalizeDataRoot(path);
        RejectReparsePoints(fullPath);
        var volume = new DriveInfo(Path.GetPathRoot(fullPath)!);
        if (volume.DriveType != DriveType.Fixed || !string.Equals(volume.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Application data requires a fixed local NTFS volume.");

        // Create missing ancestors with their ACL in place, not create-then-restrict.
        var missing = new Stack<DirectoryInfo>();
        var directory = new DirectoryInfo(fullPath);
        for (var current = directory; current is not null && !current.Exists; current = current.Parent)
            missing.Push(current);
        while (missing.TryPop(out var current))
        {
            RejectReparsePoints(current.FullName);
            current.Create(DirectoryAcl());
            RejectReparsePoints(current.FullName);
        }

        RejectReparsePoints(fullPath);
        RequireOwned(directory.GetAccessControl(AccessControlSections.Owner));
        directory.SetAccessControl(DirectoryAcl());
        RejectReparsePoints(fullPath);
    }

    public static bool WinFspInstalled
    {
        get
        {
            // Installation evidence only: this is not proof that a mount/driver load succeeds.
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = machine.OpenSubKey(@"SOFTWARE\WinFsp");
                    if (key?.GetValue("InstallDir") is not string installation || string.IsNullOrWhiteSpace(installation)) continue;
                    var architecture = RuntimeInformation.ProcessArchitecture switch
                    {
                        Architecture.X64 => "x64",
                        Architecture.X86 => "x86",
                        Architecture.Arm64 => "a64",
                        _ => null
                    };
                    if (architecture is null) continue;
                    if (HasWinFspLibrary(installation, architecture)) return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                    // Unreadable/malformed installation evidence is not a positive detection.
                }
            }
            return false;
        }
    }

    internal static bool HasWinFspLibrary(string installation, string architecture)
    {
        var root = NormalizeDataRoot(installation);
        RejectReparsePoints(root);

        var bin = Path.Combine(root, "bin");
        var attributes = File.GetAttributes(bin);
        if ((attributes & FileAttributes.Directory) == 0) return false;

        string libraryDirectory;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // WinFsp 2.1's official installer exposes bin as a junction to its
            // administrator-owned side-by-side payload. Follow only that narrow
            // layout; never accept a registry-selected link to an arbitrary path.
            var target = Directory.ResolveLinkTarget(bin, returnFinalTarget: true);
            if (target is null) return false;
            libraryDirectory = NormalizeDataRoot(target.FullName);
            var sideBySideRoot = Path.Combine(root, "SxS") + Path.DirectorySeparatorChar;
            if (!libraryDirectory.StartsWith(sideBySideRoot, StringComparison.OrdinalIgnoreCase)) return false;
            RejectReparsePoints(libraryDirectory);
        }
        else
        {
            RejectReparsePoints(bin);
            libraryDirectory = bin;
        }

        var library = Path.Combine(libraryDirectory, $"winfsp-{architecture}.dll");
        RejectReparsePoints(library);
        return File.Exists(library);
    }

    internal static string NormalizeDataRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl)) throw new ArgumentException("Invalid application directory.");
        if (path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part is not "." and not ".." && (part.EndsWith(' ') || part.EndsWith('.'))))
            throw new ArgumentException("Application directory components cannot end in a space or dot.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length <= 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' ||
            full.AsSpan(2).ContainsAny(':', '*', '?') || full.IndexOfAny(['"', '<', '>', '|']) >= 0 ||
            full[3..].Split('\\').Any(part => part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("Choose a local application directory, not a share, device path, or volume root.");
        return full;
    }

    internal static void RejectReparsePoints(string path)
    {
        var full = NormalizeDataRoot(path);
        var current = Path.GetPathRoot(full)!;
        Check(current);
        foreach (var component in full[current.Length..].Split('\\'))
        {
            current = Path.Combine(current, component);
            if (!Check(current)) break;
        }

        static bool Check(string candidate)
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not allowed in application-owned paths.");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
    }

    internal static bool FileExists(string path)
    {
        RejectReparsePoints(path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("An application file was replaced by a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void SecureFile(string path)
    {
        RejectReparsePoints(path);
        var file = new FileInfo(path);
        RequireOwned(file.GetAccessControl(AccessControlSections.Owner));
        file.SetAccessControl(FileAcl());
    }

    internal static FileSecurity FileAcl()
    {
        var security = new FileSecurity();
        var user = new SecurityIdentifier(CurrentSid);
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    private static DirectorySecurity DirectoryAcl()
    {
        var security = new DirectorySecurity();
        var user = new SecurityIdentifier(CurrentSid);
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void RequireOwned(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner?.Value != CurrentSid && owner?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("The application directory or file has an unexpected owner.");
    }
}