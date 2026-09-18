using ContainerToDrive.Windows;
using ContainerToDrive.Core;
using Microsoft.Win32;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class DataLocationTests
{
    [Fact]
    public void LocationChangeRequiresACompleteIdleNonElevatedSnapshot()
    {
        var profile = new Profile();
        var snapshot = new AppSnapshot { Profiles = [profile], Mounts = [new() { ProfileId = profile.Id, Phase = MountPhase.Unmounted }] };
        Assert.True(DataRelocation.CanRelocate(snapshot));
        Assert.False(DataRelocation.CanRelocate(null));
        Assert.False(DataRelocation.CanRelocate(snapshot with { Elevated = true }));
        Assert.False(DataRelocation.CanRelocate(snapshot with { EngineWorkerCount = 1 }));
        Assert.False(DataRelocation.CanRelocate(snapshot with { Mounts = [] }));
        Assert.False(DataRelocation.CanRelocate(snapshot with { Mounts = [new() { ProfileId = profile.Id, RecoveryRequired = true }] }));
        Assert.False(DataRelocation.CanRelocate(snapshot with { Mounts = [new() { ProfileId = profile.Id, Phase = MountPhase.Mounted }] }));
    }

    [Theory]
    [InlineData(@"C:\data", @"c:\DATA\")]
    [InlineData(@"C:\data", @"C:\data\child")]
    [InlineData(@"C:\data\child", @"C:\data")]
    [InlineData(@"C:\data", @"relative\folder")]
    [InlineData(@"C:\data", @"\\server\share\data")]
    [InlineData(@"C:\data", @"D:\")]
    public void LocationRejectsSameNestedRelativeAndUnsupportedPaths(string source, string destination)
    {
        Assert.Throws<ArgumentException>(() => DataLocation.ValidateSeparatePaths(source, destination));
    }

    [Fact]
    public void SeparateSiblingPathsAreAllowed()
    {
        DataLocation.ValidateSeparatePaths(@"C:\data", @"C:\data-new");
        DataLocation.ValidateSeparatePaths(@"C:\data", @"D:\ContainerToDrive");
    }

    [Fact]
    public void SavedRedirectFollowsOriginalShortcutWithoutChangingUnrelatedLocations()
    {
        var registryPath = @"Software\ContainerToDrive.Tests\Location-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "ContainerToDrive-location-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var third = Path.Combine(root, "third");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(third);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            Assert.Equal(first, DataLocation.Resolve(first, key));
            DataLocation.Remember(first, second, key);
            Assert.Equal(second, DataLocation.Resolve(first, key));
            DataLocation.Remember(second, third, key);
            Assert.Equal(third, DataLocation.Resolve(first, key));
            Assert.Equal(Path.Combine(root, "unrelated"), DataLocation.Resolve(Path.Combine(root, "unrelated"), key));
            Assert.Throws<IOException>(() => DataLocation.Remember(third, first, key));
            Directory.Delete(third);
            Assert.Throws<DirectoryNotFoundException>(() => DataLocation.Resolve(first, key));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }
}