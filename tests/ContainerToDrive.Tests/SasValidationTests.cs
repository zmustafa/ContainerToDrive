using ContainerToDrive.Core;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class SasValidationTests
{
    [Fact]
    public void ParsesContainerAndDecodedExpiryWithoutReturningTheQuery()
    {
        var info = Validation.ParseSas(Synthetic.Sas(), Synthetic.Now);
        Assert.Equal(Synthetic.Endpoint, info.Endpoint);
        Assert.Equal(Synthetic.Container, info.Container);
        Assert.Equal(Synthetic.Expiry, info.ExpiresAt);
        Assert.Equal("rl", info.Permissions);
        Assert.DoesNotContain(Synthetic.Signature, info.ToString());
        Assert.DoesNotContain('?', info.Endpoint);
    }

    [Fact]
    public void StoredPolicyKeepsOmittedExpiryAndPermissionsUnknown()
    {
        var info = Validation.ParseSas(Synthetic.Sas("si=synthetic-policy"), Synthetic.Now);
        Assert.Null(info.ExpiresAt);
        Assert.Null(info.Permissions);
    }

    [Fact]
    public void StoredPolicyWithInlinePermissionsStillHasUnknownExpiry()
    {
        var info = Validation.ParseSas(Synthetic.Sas("si=synthetic-policy&sp=rl"), Synthetic.Now);
        Assert.Null(info.ExpiresAt);
        Assert.Equal("rl", info.Permissions);
    }

    [Fact]
    public void EncodedSignatureAndAdditionalDelegationFieldsDoNotChangeInput()
    {
        var value = Synthetic.Sas("sp=rl&se=2030-01-01T13%3A00%3A00Z&skoid=synthetic&custom=a%2Bb%2Fc%3D")
            .Replace(Synthetic.Signature, "SYNTHETIC%2B%2F%3D", StringComparison.Ordinal);
        var original = value;
        var info = Validation.ParseSas(value, Synthetic.Now);
        Assert.Equal(original, value);
        Assert.Equal(Synthetic.Expiry, info.ExpiresAt);
        Assert.Equal("rl", info.Permissions);
        // This tests parsing only, not preservation when later provisioning an rclone remote.
    }

    [Theory]
    [InlineData("sig=duplicate")]
    [InlineData("%73ig=duplicate")]
    [InlineData("sr=c")]
    [InlineData("sp=r&sp=l")]
    [InlineData("se=2030-01-01&se=2031-01-01")]
    [InlineData("custom=one&custom=two")]
    [InlineData("missing-equals")]
    public void RejectsDuplicateOrMalformedQueryFields(string extra) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas(extra), Synthetic.Now));

    [Theory]
    [InlineData("http", "ctdsyntheticunit000.blob.core.windows.net")]
    [InlineData("https", "example.invalid")]
    [InlineData("https", "ctdsyntheticunit000.blob.core.windows.net.example.invalid")]
    [InlineData("https", "ctdsyntheticunit000.dfs.core.windows.net")]
    [InlineData("https", "ctdsyntheticunit000.blob.core.usgovcloudapi.net")]
    [InlineData("https", "127.0.0.1")]
    [InlineData("https", "localhost")]
    public void RejectsUnsupportedProductionEndpoint(string scheme, string host)
    {
        var value = $"{scheme}://{host}/{Synthetic.Container}?sr=c&sig={Synthetic.Signature}&sp=rl";
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(value, Synthetic.Now));
    }

    [Theory]
    [InlineData(":444", "", "")]
    [InlineData("", "synthetic-user@", "")]
    [InlineData("", "", "#fragment")]
    public void RejectsUnexpectedPortUserInfoOrFragment(string port, string userInfo, string fragment)
    {
        var value = $"https://{userInfo}ctdsyntheticunit000.blob.core.windows.net{port}/{Synthetic.Container}?sr=c&sig={Synthetic.Signature}&sp=rl{fragment}";
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(value, Synthetic.Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Uppercase")]
    [InlineData("has--double")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("container/blob.txt")]
    public void RejectsNonContainerPaths(string path) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(
            $"{Synthetic.Endpoint}/{path}?sr=c&sig={Synthetic.Signature}&sp=rl", Synthetic.Now));

    [Theory]
    [InlineData("sr=b&sig=synthetic&sp=rl")]
    [InlineData("ss=b&srt=sco&sig=synthetic&sp=rl")]
    [InlineData("sr=c&ss=b&sig=synthetic&sp=rl")]
    [InlineData("sr=c&sp=rl")]
    [InlineData("sr=c&sig=&sp=rl")]
    [InlineData("sr=c&sig=%20&sp=rl")]
    public void RequiresSignedContainerScope(string query) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(
            $"{Synthetic.Endpoint}/{Synthetic.Container}?{query}", Synthetic.Now));

    [Theory]
    [InlineData("r")]
    [InlineData("l")]
    [InlineData("wcd")]
    [InlineData("")]
    [InlineData("RL")]
    public void BrowsingRequiresBothReadAndListWhenInlinePermissionsExist(string permissions) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas("sp=" + permissions), Synthetic.Now));

    [Theory]
    [InlineData("rl")]
    [InlineData("lr")]
    [InlineData("racwdl")]
    public void ExtraWritePermissionsDoNotImplyWritableMount(string permissions)
    {
        Assert.Equal(permissions, Validation.ParseSas(Synthetic.Sas("sp=" + permissions), Synthetic.Now).Permissions);
        Assert.True(Synthetic.Profile().ReadOnly);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https,http")]
    public void RejectsNonHttpsOnlyProtocol(string protocol) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas("sp=rl&spr=" + protocol), Synthetic.Now));

    [Fact]
    public void AcceptsExplicitHttpsOnlyProtocol() =>
        Assert.Equal("rl", Validation.ParseSas(Synthetic.Sas("sp=rl&spr=https"), Synthetic.Now).Permissions);

    [Theory]
    [InlineData("2030-01-01T11:59:59Z")]
    [InlineData("2030-01-01T12:00:00Z")]
    [InlineData("not-a-date")]
    public void RejectsExpiredOrInvalidExpiryUsingInjectedClock(string expiry) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas("sp=rl&se=" + expiry), Synthetic.Now));

    [Fact]
    public void AcceptsEquivalentExpiryWithExplicitTimezoneOffset()
    {
        var info = Validation.ParseSas(Synthetic.Sas("sp=rl&se=2030-01-01T15:00:00%2B02:00"), Synthetic.Now);
        Assert.Equal(Synthetic.Expiry, info.ExpiresAt);
    }

    [Fact]
    public void RejectsStartTimeBeyondClockSkewAllowance() =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas("sp=rl&st=2030-01-01T12:05:01Z"), Synthetic.Now));

    [Fact]
    public void AllowsStartTimeAtClockSkewBoundary() =>
        Assert.Equal("rl", Validation.ParseSas(Synthetic.Sas("sp=rl&st=2030-01-01T12:05:00Z"), Synthetic.Now).Permissions);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not an absolute URI")]
    [InlineData("\n")]
    public void RejectsEmptyMalformedAndControlInput(string value) =>
        Assert.Throws<ArgumentException>(() => Validation.ParseSas(value, Synthetic.Now));

    [Fact]
    public void RejectsOversizedInputWithoutEchoingIt()
    {
        var exception = Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas() + new string('x', 16385), Synthetic.Now));
        Assert.DoesNotContain(Synthetic.Signature, exception.Message);
        Assert.DoesNotContain(Synthetic.Endpoint, exception.Message);
    }

    [Fact]
    public void ExpiryDiagnosticsNeverEchoCredential()
    {
        var exception = Assert.Throws<ArgumentException>(() => Validation.ParseSas(Synthetic.Sas("sp=rl&se=2000-01-01"), Synthetic.Now));
        Assert.DoesNotContain(Synthetic.Signature, exception.Message);
        Assert.DoesNotContain("sig=", exception.Message);
    }
}