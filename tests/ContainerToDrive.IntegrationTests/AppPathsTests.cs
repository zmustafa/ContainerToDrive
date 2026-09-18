using System.Security.AccessControl;
using System.Security.Principal;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class AppPathsTests
{
    [Fact]
    public void DependencyVerificationRejectsTamperedBytes()
    {
        var root = LocalTestRoot.Create();
        var path = root.PathFor("synthetic-dependency.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(path, bytes);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        DependencySetup.VerifyFile(path, expected.ToLowerInvariant());
        File.WriteAllBytes(path, [1, 2, 3, 5]);
        Assert.Throws<InvalidDataException>(() => DependencySetup.VerifyFile(path, expected));
    }

    [Fact]
    public void ExplicitDataRootStaysUnderDiscoveredWorkspaceAndNormalizesAliases()
    {
        var root = LocalTestRoot.Create();
        Assert.Equal(Path.Combine(root.Workspace, ".local", "test-runs"), Path.GetDirectoryName(root.Root));
        Assert.True(Guid.TryParseExact(Path.GetFileName(root.Root), "N", out _));
        Assert.Equal(root.Root, AppPaths.ResolveDataRoot(["--data-root", root.Root + "\\"]));
        Assert.Equal(root.Root, AppPaths.ResolveDataRoot(["--data-root=" + root.Root + "\\.\\"]));
        Assert.Equal(AppPaths.SessionKey(root.Root), AppPaths.SessionKey(root.Root.ToUpperInvariant() + "\\"));
        Assert.Equal(AppPaths.PipeName(root.Root), AppPaths.PipeName(root.Root + "\\.\\"));
        Assert.NotEqual(AppPaths.SessionKey(root.Root), AppPaths.SessionKey(root.PathFor("different-data-root")));
        Assert.NotEqual(AppPaths.SessionKey(root.Root), AppPaths.LegacySessionKey(root.Root));
    }

    [Fact]
    public void HeldLegacyControllerLockIsDetectedWithoutDeletingIt()
    {
        var root = LocalTestRoot.Create();
        var runtime = root.PathFor("runtime");
        AppPaths.SecureDirectory(runtime);
        var path = Path.Combine(runtime, "controller-" + AppPaths.LegacySessionKey(root.Root) + ".lock");
        using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(AppPaths.LegacyControllerIsRunning(root.Root));
            Assert.True(File.Exists(path));
        }
        Assert.False(AppPaths.LegacyControllerIsRunning(root.Root));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LegacyDefaultDataRootMovesWithoutCopyingContents()
    {
        var root = LocalTestRoot.Create();
        var localApplicationData = root.PathFor("local-app-data");
        Directory.CreateDirectory(localApplicationData);
        var legacy = Path.Combine(localApplicationData, "BlobToDrive");
        Directory.CreateDirectory(legacy);
        var marker = Path.Combine(legacy, "migration-marker.txt");
        File.WriteAllText(marker, "synthetic migration marker");

        var current = AppPaths.ResolveDefaultDataRoot(localApplicationData);

        Assert.Equal(Path.Combine(localApplicationData, "ContainerToDrive"), current);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("synthetic migration marker", File.ReadAllText(Path.Combine(current, "migration-marker.txt")));
    }

    [Fact]
    public void VolumeRootIsRejectedWithoutChangingVolumePermissions()
    {
        var root = LocalTestRoot.Create();
        var volume = Path.GetPathRoot(root.Root)!;
        // Normalization only: never attempt to set a volume's ACL, even as a negative test.
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root", volume]));
    }

    [Theory]
    [InlineData(@"\\synthetic-invalid\share\data")]
    [InlineData(@"\\?\C:\synthetic-data")]
    [InlineData(@"\\.\C:\synthetic-data")]
    public void SharesAndDevicePathsAreRejectedLexicallyWithoutNetworkAccess(string path)
    {
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root", path]));
    }

    [Theory]
    [InlineData("child:stream")]
    [InlineData("wild*card")]
    [InlineData("wild?card")]
    // Deliberate regression gates: AppPaths currently validates AFTER GetFullPath trims these suffixes.
    // See README for source locations and a proposed fix; do not skip or normalize the inputs in the test.
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("invalid\npath")]
    public void AmbiguousOrInvalidComponentsAreRejectedBeforeCreatingAnything(string component)
    {
        var root = LocalTestRoot.Create();
        // Deliberately do not pass hostile syntax to the independent fixture path helper.
        var path = root.Root + "\\" + component;
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root", path]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Root));
    }

    [Fact]
    public void MissingEmptyAndDuplicateExplicitOptionsFailInsteadOfFallingBackToGlobalData()
    {
        var root = LocalTestRoot.Create();
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root"]));
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root="]));
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root", "--other"]));
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveDataRoot(["--data-root", root.Root, "--data-root=" + root.Root]));
    }

    [Fact]
    public void SecureDirectoryCreatesOwnedProtectedAclOnANestedLocalNtfsPath()
    {
        var root = LocalTestRoot.Create();
        var path = root.PathFor("nested", "owned");
        AppPaths.SecureDirectory(path);
        AppPaths.SecureDirectory(path);
        Assert.True(Directory.Exists(path));
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(AppPaths.CurrentSid, acl.GetOwner(typeof(SecurityIdentifier))!.Value);
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Collection(rules.OrderBy(rule => rule.IdentityReference.Value, StringComparer.Ordinal),
            rule => AssertOwnedRule(rule, "S-1-5-18"),
            rule => AssertOwnedRule(rule, AppPaths.CurrentSid));
    }

    [Fact]
    public void ReparsePointAndItsDescendantsAreRejectedWithoutChangingTheTarget()
    {
        var root = LocalTestRoot.Create();
        var target = root.PathFor("junction-target");
        AppPaths.SecureDirectory(target);
        var sentinel = Path.Combine(target, "synthetic-sentinel.txt");
        File.WriteAllText(sentinel, "preserve this synthetic target");
        var aclBefore = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorBinaryForm();
        using (var junction = LocalJunction.Create(root, "junction", "junction-target"))
        {
            Assert.True((File.GetAttributes(junction.Path) & FileAttributes.ReparsePoint) != 0);
            var resolved = Directory.ResolveLinkTarget(junction.Path, returnFinalTarget: true);
            Assert.NotNull(resolved);
            Assert.Equal(target, resolved.FullName, ignoreCase: true);
            Assert.Throws<IOException>(() => AppPaths.SecureDirectory(junction.Path));
            Assert.Throws<IOException>(() => AppPaths.SecureDirectory(Path.Combine(junction.Path, "must-not-exist")));
            Assert.Throws<IOException>(() => new ProfileStore(junction.Path));
        }
        Assert.Equal("preserve this synthetic target", File.ReadAllText(sentinel));
        Assert.Equal(aclBefore, new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.False(Directory.Exists(Path.Combine(target, "must-not-exist")));
        Assert.Equal(sentinel, Assert.Single(Directory.EnumerateFileSystemEntries(target)));
        Assert.False(Directory.Exists(root.PathFor("junction")));
    }

    [Fact]
    public void WinFspDetectionAcceptsDirectAndContainedSideBySideLayoutsOnly()
    {
        var root = LocalTestRoot.Create();

        var direct = root.PathFor("winfsp-direct", "bin");
        AppPaths.SecureDirectory(direct);
        File.WriteAllBytes(Path.Combine(direct, "winfsp-x64.dll"), [1]);
        Assert.True(AppPaths.HasWinFspLibrary(root.PathFor("winfsp-direct"), "x64"));

        var sideBySide = root.PathFor("winfsp-sxs", "SxS", "sxs.test", "bin");
        AppPaths.SecureDirectory(sideBySide);
        File.WriteAllBytes(Path.Combine(sideBySide, "winfsp-x64.dll"), [1]);
        using (var junction = LocalJunction.Create(root, Path.Combine("winfsp-sxs", "bin"), Path.Combine("winfsp-sxs", "SxS", "sxs.test", "bin")))
            Assert.True(AppPaths.HasWinFspLibrary(root.PathFor("winfsp-sxs"), "x64"));

        var outside = root.PathFor("outside-bin");
        AppPaths.SecureDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "winfsp-x64.dll"), [1]);
        AppPaths.SecureDirectory(root.PathFor("winfsp-external"));
        using (var junction = LocalJunction.Create(root, Path.Combine("winfsp-external", "bin"), "outside-bin"))
            Assert.False(AppPaths.HasWinFspLibrary(root.PathFor("winfsp-external"), "x64"));
    }

    private static void AssertOwnedRule(FileSystemAccessRule rule, string sid)
    {
        Assert.Equal(sid, rule.IdentityReference.Value);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.False(rule.IsInherited);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
    }
}