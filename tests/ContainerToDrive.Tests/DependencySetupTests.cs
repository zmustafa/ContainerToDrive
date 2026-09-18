using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class DependencySetupTests
{
    [Theory]
    [InlineData("https://downloads.rclone.org/v1.75.1/archive.zip", true)]
    [InlineData("https://github.com/winfsp/winfsp/releases/download/v2.1/setup.msi", true)]
    [InlineData("https://release-assets.githubusercontent.com/asset?signature=synthetic", true)]
    [InlineData("https://objects.githubusercontent.com/asset", true)]
    [InlineData("http://downloads.rclone.org/archive.zip", false)]
    [InlineData("https://github.com:8443/asset", false)]
    [InlineData("https://user@github.com/asset", false)]
    [InlineData("https://github.com/asset#fragment", false)]
    [InlineData("https://github.com.synthetic.invalid/asset", false)]
    [InlineData("https://synthetic.invalid/asset", false)]
    [InlineData("file:///C:/synthetic/asset", false)]
    [InlineData("/relative/asset", false)]
    public void DownloadOriginsAreValidatedWithoutNetworkAccess(string value, bool expected) =>
        Assert.Equal(expected, DependencySetup.IsApprovedDownloadUri(new Uri(value, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void DependencyVersionsComeFromEmbeddedPinnedManifest()
    {
        Assert.Equal("1.75.1", DependencySetup.EngineVersion);
        Assert.Equal("2.1.25156", DependencySetup.WinFspVersion);
    }
}