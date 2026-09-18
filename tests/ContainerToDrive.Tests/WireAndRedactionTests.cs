using System.Text.Json;
using ContainerToDrive.Core;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class WireAndRedactionTests
{
    [Fact]
    public void SnapshotReportsThePackagedAssemblyVersion()
    {
        var assembly = typeof(AppSnapshot).Assembly.GetName().Version;
        Assert.NotNull(assembly);
        Assert.Equal($"{assembly.Major}.{assembly.Minor}.{assembly.Build}-preview", new AppSnapshot().Version);
    }

    [Fact]
    public void ProfileJsonRoundTripPreservesAllPersistedFieldsAndNoCredentialField()
    {
        var original = Synthetic.Profile() with
        {
            Revision = 7, Name = "Synthetic 日本語", Prefix = "Data/2026", DriveLetter = "Y",
            ReadOnly = false, AutoMount = true, CacheMaxBytes = 512L * 1024 * 1024,
            MinFreeBytes = 2L * 1024 * 1024 * 1024,
            ExpiresAt = Synthetic.Expiry.AddTicks(1234560)
        };
        var json = JsonSerializer.Serialize(original, Wire.Json);
        var copy = JsonSerializer.Deserialize<Profile>(json, Wire.Json);
        Assert.Equal(original, copy);
        Assert.Equal(json, JsonSerializer.Serialize(copy, Wire.Json));
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("sas", out _));
        Assert.False(document.RootElement.TryGetProperty("password", out _));
        Assert.DoesNotContain(Synthetic.Signature, json);
    }

    [Fact]
    public void MissingTelemetryRemainsUnknownNotZero()
    {
        var status = JsonSerializer.Deserialize<MountStatus>("{}", Wire.Json);
        Assert.NotNull(status);
        Assert.Equal(UploadState.Unknown, status.Uploads);
        Assert.Null(status.ObservedAt);
        Assert.Null(status.CacheBytes);
        Assert.Null(status.Queued);
        Assert.Null(status.Uploading);
    }

    [Fact]
    public void StatusRoundTripPreservesFailureQueueAndRecoveryState()
    {
        var original = new MountStatus
        {
            ProfileId = Synthetic.ProfileId, Phase = MountPhase.Faulted, Uploads = UploadState.Failed,
            Message = "Synthetic failure", ObservedAt = Synthetic.Now, CacheBytes = 42,
            Queued = 1, Uploading = 0, RecoveryRequired = true,
            Queue = [new UploadItem("synthetic.txt", 42, false, 3)]
        };
        var json = JsonSerializer.Serialize(original, Wire.Json);
        var copy = JsonSerializer.Deserialize<MountStatus>(json, Wire.Json);
        Assert.NotNull(copy);
        Assert.Equal(json, JsonSerializer.Serialize(copy, Wire.Json));
        Assert.Equal(original.Queue[0], Assert.Single(copy.Queue));
        Assert.True(copy.RecoveryRequired);
        Assert.Equal(UploadState.Failed, copy.Uploads);
        Assert.Contains("\"Faulted\"", json);
    }

    [Fact]
    public void RequestRoundTripRetainsProtocolAndExplicitConsentFlags()
    {
        var original = new Request
        {
            Operation = "SaveProfile", ProfileId = Synthetic.ProfileId, Profile = Synthetic.Profile(),
            Credential = CredentialSubmission.ContainerSas(Synthetic.Sas()), Confirm = true, Force = false
        };
        var copy = JsonSerializer.Deserialize<Request>(JsonSerializer.Serialize(original, Wire.Json), Wire.Json);
        Assert.Equal(original, copy);
        Assert.Equal(Wire.Version, original.ProtocolVersion);
        Assert.NotEqual(Guid.Empty, original.RequestId);
        // The request deliberately carries synthetic secret data. It must never be logged or persisted.
    }

    [Fact]
    public void UnknownProtocolVersionIsNotSilentlyRewrittenBySerializer()
    {
        var request = JsonSerializer.Deserialize<Request>("{\"protocolVersion\":999}", Wire.Json);
        Assert.NotNull(request);
        Assert.Equal(999, request.ProtocolVersion);
        // Controller rejection belongs in its own protocol tests once that API exists.
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"uploads\":\"InventedHealthyState\"}")]
    [InlineData("{\"queued\":\"not-a-number\"}")]
    public void MalformedTelemetryCannotDeserializeAsHealthy(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MountStatus>(json, Wire.Json));

    [Fact]
    public void ResponseFactoriesKeepSuccessAndFailureDistinct()
    {
        Assert.True(Response.Ok().Success);
        var failure = Response.Fail("Synthetic failure");
        Assert.False(failure.Success);
        Assert.Null(failure.Snapshot);
        Assert.Equal("Synthetic failure", failure.Message);
    }

    [Fact]
    public void MountIntentDefaultsToUncertainAndRoundTrips()
    {
        var intent = new MountIntent
        {
            ProfileId = Synthetic.ProfileId, Identity = Validation.Identity(Synthetic.Profile()),
            EngineVersion = "1.75.1", WasWritable = true, UpdatedAt = Synthetic.Now
        };
        Assert.True(intent.Uncertain);
        Assert.Equal(intent, JsonSerializer.Deserialize<MountIntent>(JsonSerializer.Serialize(intent, Wire.Json), Wire.Json));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Ordinary status message", "Ordinary status message")]
    public void RedactionHandlesNullEmptyAndOrdinaryMessages(string? input, string expected) =>
        Assert.Equal(expected, Redaction.Clean(input));

    [Fact]
    public void RedactionRemovesWholeSasUrlNotJustSignature()
    {
        var clean = Redaction.Clean("Failed to open " + Synthetic.Sas() + " retry later");
        Assert.Equal("Failed to open [storage URL redacted] retry later", clean);
        Assert.DoesNotContain(Synthetic.Container, clean);
        Assert.DoesNotContain(Synthetic.Signature, clean);
    }

    [Theory]
    [InlineData("sig=synthetic-value")]
    [InlineData("SAS_URL:synthetic-value")]
    [InlineData("password = synthetic-value")]
    [InlineData("authorization:synthetic-value")]
    [InlineData("TOKEN=synthetic-value")]
    [InlineData("secret:synthetic-value")]
    public void RedactsSupportedStandaloneSecretFields(string input)
    {
        var clean = Redaction.Clean(input);
        Assert.DoesNotContain("synthetic-value", clean);
        Assert.Contains("[redacted]", clean);
    }

    [Theory]
    [InlineData("sig")]
    [InlineData("sas_url")]
    [InlineData("password")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("secret")]
    public void QuotedJsonSecretValuesCannotLeak(string field)
    {
        // Regression gate: a quoted field name must not bypass standalone-field redaction.
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "SYNTHETIC-SECRET-VALUE" });
        Assert.DoesNotContain("SYNTHETIC-SECRET-VALUE", Redaction.Clean(json));
    }

    [Fact]
    public void BearerAuthorizationRedactsTheCredentialNotOnlyTheScheme()
    {
        // Regression gate: removing only the word "Bearer" leaves the credential exposed.
        Assert.DoesNotContain("SYNTHETIC-BEARER-VALUE", Redaction.Clean("Authorization: Bearer SYNTHETIC-BEARER-VALUE"));
    }

    [Fact]
    public void RedactsMultipleUrlsAndFieldsBeforeTruncation()
    {
        var clean = Redaction.Clean(Synthetic.Sas() + " token=synthetic-value " + Synthetic.Sas() + new string('x', 3000));
        Assert.DoesNotContain(Synthetic.Signature, clean);
        Assert.DoesNotContain("synthetic-value", clean);
        Assert.True(clean.Length <= 2000);
    }

    [Fact]
    public void LongNonSecretMessageIsBounded() =>
        Assert.Equal(new string('x', 2000), Redaction.Clean(new string('x', 2001)));

    [Fact]
    public void PersonalHomePathIsRemovedCaseInsensitively()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home));
        Assert.Equal("[user]\\synthetic.txt", Redaction.Clean(home.ToUpperInvariant() + "\\synthetic.txt"));
    }

    [Fact]
    public void RedactionIsIdempotent()
    {
        var once = Redaction.Clean("sig=synthetic-value " + Synthetic.Sas());
        Assert.Equal(once, Redaction.Clean(once));
    }
}