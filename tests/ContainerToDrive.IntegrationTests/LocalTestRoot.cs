using System.Text;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

internal sealed class LocalTestRoot
{
    private LocalTestRoot(string workspace, string root)
    {
        Workspace = workspace;
        Root = root;
    }

    internal string Workspace { get; }
    internal string Root { get; }

    internal static LocalTestRoot Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("LocalIntegration requires Windows; this is a failure, not a skip.");

        // Deliberately no CWD, environment, temp, home, installed-app, or global-data fallback.
        DirectoryInfo? workspace = null;
        for (var ancestor = new DirectoryInfo(AppContext.BaseDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (File.Exists(Path.Combine(ancestor.FullName, "ContainerToDrive.slnx")) &&
                File.Exists(Path.Combine(ancestor.FullName, "src", "ContainerToDrive.Windows", "ContainerToDrive.Windows.csproj")) &&
                File.Exists(Path.Combine(ancestor.FullName, "tests", "ContainerToDrive.IntegrationTests", "ContainerToDrive.IntegrationTests.csproj")))
            {
                workspace = ancestor;
                break;
            }
        }
        if (workspace is null)
            throw new DirectoryNotFoundException("Run the integration assembly from this checkout's artifacts tree; no workspace ancestor was found.");

        var root = Path.Combine(workspace.FullName, ".local", "test-runs", Guid.NewGuid().ToString("N"));
        RequireOrdinaryAncestors(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The fresh test root already exists.");
        // Creation with the explicit user ACL also works when the test host is elevated.
        // Never secure the workspace or an already existing shared parent itself.
        AppPaths.SecureDirectory(root);
        RequireOrdinaryAncestors(root);
        return new LocalTestRoot(workspace.FullName, root);
    }

    internal string PathFor(params string[] components)
    {
        var full = Path.GetFullPath(Path.Combine(new[] { Root }.Concat(components).ToArray()));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A test path escaped its generated root.");
        RequireOrdinaryAncestors(full);
        return full;
    }

    internal ProfileStore OpenStore() => new(Root);

    internal void AssertNoPlaintext(IEnumerable<SyntheticCredential> credentials, string? activeEmptyLock = null)
    {
        var secrets = credentials.ToArray();
        foreach (var file in EnumerateOrdinaryFiles(Root))
        {
            if (activeEmptyLock is not null && string.Equals(file, activeEmptyLock, StringComparison.OrdinalIgnoreCase))
            {
                // Rclone holds this exact file with FileShare.None. Do not silently ignore unreadable files.
                // Its zero length is asserted here; it is opened and scanned after worker disposal as well.
                Assert.Equal(0L, new FileInfo(file).Length);
                continue;
            }
            var bytes = File.ReadAllBytes(file);
            foreach (var secret in secrets)
                AssertNoPlaintext(bytes, secret);
        }
    }

    internal static void AssertNoPlaintext(byte[] bytes, SyntheticCredential credential)
    {
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            // The ASCII signature marker also survives JSON escaping and URL encoding of the rest of the SAS.
            Assert.True(bytes.AsSpan().IndexOf(encoding.GetBytes(credential.Signature)) < 0,
                "A generated file or captured message contains the synthetic SAS signature in plaintext.");
            Assert.True(bytes.AsSpan().IndexOf(encoding.GetBytes(credential.Url)) < 0,
                "A generated file or captured message contains the synthetic SAS URL in plaintext.");
        }
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string directory)
    {
        RequireOrdinaryAncestors(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to follow a reparse point while inspecting test output.");
            if ((attributes & FileAttributes.Directory) == 0) yield return entry;
            else foreach (var file in EnumerateOrdinaryFiles(entry)) yield return file;
        }
    }

    private static void RequireOrdinaryAncestors(string path)
    {
        // Independent of AppPaths: a regression in the API under test must not redirect test writes.
        var full = Path.GetFullPath(path);
        if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new IOException("The test checkout must be on a local drive, not a share or device path.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The test checkout/output ancestry must not contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Roots are deliberately retained, including after failures. No recursive cleanup can consume
    // real caches or follow a junction. The only junction these tests create is removed explicitly.
}