using ContainerToDrive.Windows;
using Microsoft.Win32;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class WindowsStartupTests
{
    private const string Executable = @"C:\Program Files\ContainerToDrive\ContainerToDrive.Desktop.exe";
    private const string DataRoot = @"C:\Users\Synthetic User\AppData\Local\ContainerToDrive";

    [Fact]
    public void StartupCommandQuotesPathsStartsMinimizedAndPreservesTheDataRoot()
    {
        Assert.Equal($"\"{Executable}\" --minimized --data-root \"{DataRoot}\"",
            WindowsStartup.CreateCommand(Executable, DataRoot + "\\"));
    }

    [Theory]
    [InlineData(@"C:\tools\dotnet.exe", DataRoot)]
    [InlineData(@"C:\tools\ContainerToDrive.Controller.exe", DataRoot)]
    [InlineData(@"relative\ContainerToDrive.Desktop.exe", DataRoot)]
    [InlineData(Executable, @"relative\data")]
    [InlineData(Executable, "C:\\data\" --other-option")]
    [InlineData(Executable, "C:\\data\ninvalid")]
    public void StartupRejectsUnsupportedExecutablesAndAmbiguousPaths(string executable, string dataRoot)
    {
        Assert.Throws<ArgumentException>(() => WindowsStartup.CreateCommand(executable, dataRoot));
    }

    [Fact]
    public void StartupRejectsCommandsExceedingWindowsRunLimit()
    {
        Assert.Throws<ArgumentException>(() => WindowsStartup.CreateCommand(Executable, @"C:\" + new string('a', 220)));
    }

    [Fact]
    public void RegistrationIsOptInIdempotentAndOnlyRemovesThisInstallation()
    {
        var testKey = @"Software\ContainerToDrive.Tests\Startup-" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(testKey);
            var startup = new WindowsStartup(Executable, DataRoot);
            key.SetValue("Unrelated application", "leave unchanged");
            Assert.False(startup.IsEnabled(key));
            Assert.False(startup.IsEnabled(null));
            startup.SetEnabled(key, true);
            startup.SetEnabled(key, true);
            Assert.True(startup.IsEnabled(key));
            Assert.Equal(RegistryValueKind.String, key.GetValueKind(WindowsStartup.ValueName));
            Assert.Equal(WindowsStartup.CreateCommand(Executable, DataRoot), key.GetValue(WindowsStartup.ValueName));

            var otherDataRoot = new WindowsStartup(Executable, @"C:\different-data");
            Assert.False(otherDataRoot.IsEnabled(key));
            otherDataRoot.SetEnabled(key, false);
            Assert.True(startup.IsEnabled(key));
            startup.SetEnabled(key, false);
            startup.SetEnabled(key, false);
            Assert.False(startup.IsEnabled(key));
            Assert.Null(key.GetValue(WindowsStartup.ValueName));
            Assert.Equal("leave unchanged", key.GetValue("Unrelated application"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false); }
    }
}