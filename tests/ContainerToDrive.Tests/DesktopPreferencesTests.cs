using System.Text;
using ContainerToDrive.Desktop;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class DesktopPreferencesTests
{
    [Fact]
    public void DefaultsPreserveExistingBehaviorAndNewConnectionSafety()
    {
        var preferences = new DesktopPreferences();
        preferences.Validate();
        Assert.True(preferences.CloseToTray);
        Assert.False(preferences.StartMinimized);
        Assert.True(preferences.ShowEndpointInfo);
        Assert.Equal(10, preferences.CapabilityCollapseSeconds);
        var profile = (preferences with { DefaultCacheGiB = 20, DefaultMinFreeGiB = 3 }).NewProfile(Guid.NewGuid());
        Assert.True(profile.ReadOnly);
        Assert.False(profile.AutoMount);
        Assert.Equal(20L * 1024 * 1024 * 1024, profile.CacheMaxBytes);
        Assert.Equal(3L * 1024 * 1024 * 1024, profile.MinFreeBytes);
    }

    [Fact]
    public void DraftTracksValidationAndDoesNotApplyUnsavedDefaults()
    {
        var saved = new DesktopPreferences { DefaultCacheGiB = 20 };
        var editor = new PreferencesEditor(saved);
        Assert.False(editor.HasChanges);
        editor.CacheGiB = "invalid";
        Assert.True(editor.HasError);
        Assert.True(editor.HasChanges);
        Assert.Throws<ArgumentException>(() => editor.Build());
        editor.RestoreDefaults();
        Assert.True(editor.HasChanges);
        Assert.Equal(20, saved.DefaultCacheGiB);
        editor.MarkSaved(editor.Build());
        Assert.False(editor.HasChanges);
        Assert.Throws<ArgumentException>(() => (saved with { DefaultMinFreeGiB = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (saved with { DnsRefreshSeconds = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (saved with { CapabilityCollapseSeconds = -1 }).Validate());
    }

    [Fact]
    public void ProtectedPreferencesRoundTripWithoutCreatingDataOnFirstReadOrOverwritingInvalidRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContainerToDrive-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopPreferencesStore(root);
            Assert.True(store.TryLoad(out var defaults));
            Assert.Equal(new DesktopPreferences(), defaults);
            Assert.False(Directory.Exists(root));
            var saved = defaults with { StartMinimized = true, CloseToTray = false, DnsRefreshSeconds = 120, ActivityPeriodDays = 30 };
            store.Save(saved);
            Assert.True(new DesktopPreferencesStore(root).TryLoad(out var reopened));
            Assert.Equal(saved, reopened);
            new ProtectedSettingsStore(root).Write(DesktopPreferencesStore.Name, Encoding.UTF8.GetBytes("{\"schemaVersion\":99}"));
            var path = Path.Combine(root, "settings", DesktopPreferencesStore.Name + ".bin");
            var before = File.ReadAllBytes(path);
            Assert.False(store.TryLoad(out _));
            Assert.Throws<InvalidDataException>(() => store.Save(defaults));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}