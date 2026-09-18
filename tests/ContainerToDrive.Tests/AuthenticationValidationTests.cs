using System.Text;
using ContainerToDrive.Core;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class AuthenticationValidationTests
{
    private static readonly string AccountKey = Convert.ToBase64String(Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());

    [Fact]
    public void WritableContainerSasRequiresMutationPermissions()
    {
        var profile = Synthetic.Profile() with { ReadOnly = false };
        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile,
            CredentialSubmission.ContainerSas(Synthetic.Sas()), Synthetic.Now));

        var info = Validation.ValidateCredential(profile,
            CredentialSubmission.ContainerSas(Synthetic.Sas("sp=racwdl&se=2030-01-01T13%3A00%3A00Z")), Synthetic.Now);
        Assert.Equal(AuthenticationKind.ContainerSas, info.Kind);
    }

    [Fact]
    public void AccountKeyCredentialRequiresExactKindSourceAndKeyShape()
    {
        var profile = Synthetic.Profile() with { AuthenticationKind = AuthenticationKind.AccountKey };
        var credential = CredentialSubmission.AccountKeyCredential(AccountKey);
        var info = Validation.ValidateCredential(profile, credential, Synthetic.Now);
        Assert.Equal(AuthenticationKind.AccountKey, info.Kind);
        Assert.Null(info.ExpiresAt);
        Assert.True(string.Equals(AccountKey, info.Secret, StringComparison.Ordinal), "The validated synthetic key differs; key text is not printed.");

        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile,
            credential with { SasUrl = Synthetic.Sas() }, Synthetic.Now));
        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile,
            credential with { AccountKey = "not-base64" }, Synthetic.Now));
        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile with
        {
            AuthenticationKind = AuthenticationKind.ContainerSas
        }, credential, Synthetic.Now));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("storage123")]
    [InlineData("abcdefghijklmnopqrstuvwx")]
    public void StorageAccountNamesProduceOnlyPublicCloudBlobEndpoints(string account)
    {
        Assert.Equal(account, Validation.ValidateAccountName(account));
        Assert.Equal($"https://{account}.blob.core.windows.net", Validation.EndpointForAccount(account));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("has-dash")]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    public void InvalidStorageAccountNamesAreRejected(string account) =>
        Assert.Throws<ArgumentException>(() => Validation.ValidateAccountName(account));

    [Fact]
    public void MicrosoftEntraCredentialRequiresCompleteDelegationSasAndManagementIdentity()
    {
        var profile = EntraProfile();
        var credential = CredentialSubmission.MicrosoftEntraSas(DelegationSas());
        var info = Validation.ValidateCredential(profile, credential, Synthetic.Now);
        Assert.Equal(AuthenticationKind.MicrosoftEntra, info.Kind);
        Assert.NotNull(info.ExpiresAt);
        Assert.True(Validation.ParseSas(credential.SasUrl!, Synthetic.Now).IsUserDelegation);

        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile,
            CredentialSubmission.MicrosoftEntraSas(Synthetic.Sas()), Synthetic.Now));
        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile,
            CredentialSubmission.MicrosoftEntraSas(DelegationSas("sp=rwl")), Synthetic.Now));
        Assert.Throws<ArgumentException>(() => Validation.ValidateCredential(profile with { ReadOnly = false },
            CredentialSubmission.MicrosoftEntraSas(DelegationSas()), Synthetic.Now));
        var writable = Validation.ValidateCredential(profile with { ReadOnly = false },
            CredentialSubmission.MicrosoftEntraSas(DelegationSas("sp=racwdl")), Synthetic.Now);
        Assert.Equal(AuthenticationKind.MicrosoftEntra, writable.Kind);
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(profile with { TenantId = "" }));
        Assert.Throws<ArgumentException>(() => Validation.ValidateProfile(profile with { SubscriptionId = "invalid" }));
    }

    [Fact]
    public void AuthenticationKindAndCredentialRevisionDoNotChangeCacheIdentity()
    {
        var profile = Synthetic.Profile();
        var changed = profile with
        {
            AuthenticationKind = AuthenticationKind.AccountKey,
            CredentialRevision = 99,
            ExpiresAt = Synthetic.Expiry
        };
        Assert.Equal(Validation.Identity(profile), Validation.Identity(changed));
        Assert.Equal(profile.RemoteName, changed.RemoteName);
        Assert.Equal(profile.RemotePath, changed.RemotePath);
    }

    [Fact]
    public void WireRoundTripsEachCredentialVariantWithoutChangingDiscriminator()
    {
        CredentialSubmission[] credentials =
        [
            CredentialSubmission.ContainerSas(Synthetic.Sas(), 3),
            CredentialSubmission.AccountKeyCredential(AccountKey, 4),
            CredentialSubmission.MicrosoftEntraSas(DelegationSas(), 5)
        ];
        foreach (var credential in credentials)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(credential, Wire.Json);
            var copy = System.Text.Json.JsonSerializer.Deserialize<CredentialSubmission>(json, Wire.Json);
            Assert.NotNull(copy);
            Assert.Equal(credential.Kind, copy.Kind);
            Assert.Equal(credential.ExpectedRevision, copy.ExpectedRevision);
            Assert.Equal(credential with { SasUrl = null, AccountKey = null }, copy with { SasUrl = null, AccountKey = null });
        }
    }

    [Theory]
    [InlineData("account_key=synthetic-value")]
    [InlineData("access_token:synthetic-value")]
    [InlineData("refresh_token=synthetic-value")]
    [InlineData("client_secret:synthetic-value")]
    [InlineData("client_assertion=synthetic-value")]
    public void RedactionCoversExpandedAuthenticationSecrets(string input)
    {
        var clean = Redaction.Clean(input);
        Assert.DoesNotContain("synthetic-value", clean);
        Assert.Contains("[redacted]", clean);
    }

    private static Profile EntraProfile() => Synthetic.Profile() with
    {
        AuthenticationKind = AuthenticationKind.MicrosoftEntra,
        TenantId = "11111111-1111-1111-1111-111111111111",
        SubscriptionId = "22222222-2222-2222-2222-222222222222",
        ResourceGroupName = "synthetic-rg"
    };

    private static string DelegationSas(string permissions = "sp=rl") =>
        Synthetic.Sas(permissions +
            "&se=2030-01-01T13%3A00%3A00Z" +
            "&skoid=11111111-1111-1111-1111-111111111111" +
            "&sktid=22222222-2222-2222-2222-222222222222" +
            "&skt=2030-01-01T11%3A55%3A00Z" +
            "&ske=2030-01-02T12%3A00%3A00Z&sks=b&skv=2025-01-05");
}
