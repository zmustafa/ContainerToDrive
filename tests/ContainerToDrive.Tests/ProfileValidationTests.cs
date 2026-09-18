using ContainerToDrive.Core;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class ProfileValidationTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private const long TiB = 1024L * GiB;

    [Fact]
    public void ValidProfileIsReturnedWithoutMutation()
    {
        var profile = Synthetic.Profile();
        Assert.Same(profile, Validation.ValidateProfile(profile));
    }

    [Theory]
    [InlineData(256 * MiB, GiB)]
    [InlineData(TiB, GiB)]
    [InlineData(10 * GiB, 5 * GiB)]
    public void AcceptsCacheBoundarySizes(long cache, long reserve) =>
        Assert.Equal(cache, Validation.ValidateProfile(Synthetic.Profile() with { CacheMaxBytes = cache, MinFreeBytes = reserve }).CacheMaxBytes);

    [Theory]
    [InlineData(-1, GiB)]
    [InlineData(0, GiB)]
    [InlineData(256 * MiB - 1, GiB)]
    [InlineData(TiB + 1, GiB)]
    [InlineData(long.MaxValue, GiB)]
    [InlineData(GiB, -1)]
    [InlineData(GiB, GiB - 1)]
    public void RejectsInvalidCacheAndReserveSizes(long cache, long reserve) =>
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { CacheMaxBytes = cache, MinFreeBytes = reserve }));

    [Theory]
    [InlineData("/absolute")]
    [InlineData("trailing/")]
    [InlineData("back\\slash")]
    [InlineData("C:drive")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("bad\tname")]
    public void RejectsUnsafePrefixes(string prefix) =>
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { Prefix = prefix }));

    [Theory]
    [InlineData("")]
    [InlineData("data")]
    [InlineData("Data/2026")]
    [InlineData("synthetic space/日本語")]
    public void PreservesValidPrefixesExactly(string prefix) =>
        Assert.Equal(prefix, Validation.ValidateProfile(Synthetic.Profile() with { Prefix = prefix }).Prefix);

    [Fact]
    public void EnforcesPrefixLengthBoundary()
    {
        Assert.Equal(1024, Validation.ValidateProfile(Synthetic.Profile() with { Prefix = new string('a', 1024) }).Prefix.Length);
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { Prefix = new string('a', 1025) }));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("A")]
    [InlineData("z")]
    [InlineData("Z:")]
    [InlineData("")]
    [InlineData("[")]
    public void RejectsInvalidDriveLetters(string drive) =>
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { DriveLetter = drive }));

    [Theory]
    [InlineData("D")]
    [InlineData("Z")]
    public void AcceptsDriveLetterBoundaries(string drive) =>
        Assert.Equal(drive, Validation.ValidateProfile(Synthetic.Profile() with { DriveLetter = drive }).DriveLetter);

    [Fact]
    public void RejectsEmptyIdentityAndInvalidNames()
    {
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { Id = Guid.Empty }));
        foreach (var name in new[] { "", new string('n', 101), "bad\rname" })
            Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { Name = name }));
        Assert.Equal(100, Validation.ValidateProfile(Synthetic.Profile() with { Name = new string('n', 100) }).Name.Length);
    }

    [Theory]
    [InlineData("http://ctdsyntheticunit000.blob.core.windows.net")]
    [InlineData("https://example.invalid")]
    [InlineData("https://ctdsyntheticunit000.blob.core.windows.net/container")]
    [InlineData("https://ctdsyntheticunit000.blob.core.windows.net?sig=synthetic")]
    [InlineData("https://ctdsyntheticunit000.blob.core.windows.net#fragment")]
    [InlineData("https://user@ctdsyntheticunit000.blob.core.windows.net")]
    public void PersistedProfileEndpointCannotContainCredentialsOrPaths(string endpoint) =>
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(Synthetic.Profile() with { Endpoint = endpoint }));

    [Theory]
    [InlineData("", "data", true)]
    [InlineData("data", "", true)]
    [InlineData("data", "data", true)]
    [InlineData("data", "data/child", true)]
    [InlineData("data/child/grandchild", "data", true)]
    [InlineData("data", "database", false)]
    [InlineData("data/a", "data/b", false)]
    [InlineData("Data", "data", false)]
    [InlineData("Data", "data/child", false)]
    public void OverlapUsesCaseSensitivePrefixesAndSlashBoundaries(string left, string right, bool expected)
    {
        var a = Synthetic.Profile() with { Prefix = left };
        var b = Synthetic.Profile() with { Id = Guid.NewGuid(), Prefix = right };
        Assert.Equal(expected, Validation.Overlaps(a, b));
        Assert.Equal(expected, Validation.Overlaps(b, a));
    }

    [Fact]
    public void EndpointCaseAndTrailingSlashDoNotHideOverlap()
    {
        var profile = Synthetic.Profile();
        var alternate = profile with { Endpoint = profile.Endpoint.ToUpperInvariant() + "/" };
        Assert.True(Validation.Overlaps(profile, alternate));
        Assert.Equal(Validation.Identity(profile), Validation.Identity(alternate));
    }

    [Fact]
    public void DifferentContainersAndAccountsDoNotOverlap()
    {
        var profile = Synthetic.Profile();
        Assert.False(Validation.Overlaps(profile, profile with { Container = "other-fixtures" }));
        Assert.False(Validation.Overlaps(profile, profile with { Endpoint = "https://ctdotherunit000.blob.core.windows.net" }));
    }

    [Fact]
    public void RenameDriveChangeAndCredentialExpiryDoNotChangeRemoteIdentity()
    {
        var profile = Synthetic.Profile() with { Prefix = "Data" };
        var changed = profile with { Name = "Renamed", DriveLetter = "Y", ExpiresAt = Synthetic.Expiry, Revision = 2 };
        Assert.Equal(profile.RemoteName, changed.RemoteName);
        Assert.Equal(profile.RemotePath, changed.RemotePath);
        Assert.Equal(Validation.Identity(profile), Validation.Identity(changed));
        Assert.NotEqual(Validation.Identity(profile), Validation.Identity(profile with { Prefix = "data" }));
    }

    [Fact]
    public void RemotePathUsesOpaqueIdAndHasNoTrailingSlashForWholeContainer()
    {
        var profile = Synthetic.Profile();
        Assert.Equal("ctd_78ee7f649bf041b2996540e6297d3623", profile.RemoteName);
        Assert.Equal(profile.RemoteName + ":unit-fixtures", profile.RemotePath);
        Assert.Equal(profile.RemotePath + "/Data/child", (profile with { Prefix = "Data/child" }).RemotePath);
    }
}