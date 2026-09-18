using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class ProfileStoreTests
{
    [Fact]
    public void DpapiRoundTripSurvivesReopeningAndRawRecordContainsOnlyCiphertext()
    {
        var root = LocalTestRoot.Create();
        var credential = SyntheticCredential.Create();
        var profile = SyntheticCredential.Profile() with { Name = "Synthetic 日本語", AutoMount = true };
        var saved = root.OpenStore().Save(profile, Sas(credential));
        var reopened = root.OpenStore();

        Assert.Equal(profile with { Revision = 1, CredentialRevision = 1, ExpiresAt = credential.ExpiresAt }, saved);
        Assert.Equal(saved, reopened.Find(saved.Id));
        Assert.Equal(saved, Assert.Single(reopened.List()));
        AssertSecret(reopened, saved.Id, credential);

        using var document = JsonDocument.Parse(File.ReadAllBytes(ProfilePath(root, saved.Id)));
        var record = document.RootElement;
        Assert.Equal(3, record.GetProperty("schemaVersion").GetInt32());
        Assert.False(record.GetProperty("removed").GetBoolean());
        Assert.False(record.GetProperty("profile").TryGetProperty("sas", out _));
        var ciphertext = record.GetProperty("protectedCredential").GetBytesFromBase64();
        Assert.NotEmpty(ciphertext);
        // Independently exercise CurrentUser DPAPI with the production record's identity entropy.
        var plaintext = ProtectedData.Unprotect(ciphertext,
            Encoding.UTF8.GetBytes("ContainerToDrive/credential/v3/ContainerSas/" + saved.Id.ToString("N")), DataProtectionScope.CurrentUser);
        try
        {
            var secretMatches = Encoding.UTF8.GetString(plaintext) == credential.Url;
            Assert.True(secretMatches, "DPAPI did not recover the original synthetic credential.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void MetadataSavePreservesSecretExpiryAndEncryptedBackup()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var path = ProfilePath(root, first.Id);
        var firstBytes = File.ReadAllBytes(path);
        var second = store.Save(first with { Name = "Renamed synthetic profile", DriveLetter = "Y" }, null);

        Assert.Equal(2, second.Revision);
        Assert.Equal(1, second.CredentialRevision);
        Assert.Equal(first.ExpiresAt, second.ExpiresAt);
        Assert.Equal(firstBytes, File.ReadAllBytes(path + ".bak"));
        AssertSecret(root.OpenStore(), second.Id, credential);
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void DpapiCiphertextCannotBeTransplantedToAnotherProfileId()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var second = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var firstRecord = ReadRecord(ProfilePath(root, first.Id));
        var secondPath = ProfilePath(root, second.Id);
        var secondRecord = ReadRecord(secondPath);
        secondRecord["protectedCredential"] = firstRecord["protectedCredential"]!.DeepClone();
        File.WriteAllText(secondPath, secondRecord.ToJsonString());

        Assert.Throws<CryptographicException>(() => root.OpenStore().ReadSecret(second.Id));
        AssertSecret(store, first.Id, credential);
        root.AssertNoPlaintext([credential]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StaleRevisionsCannotOverwriteACommittedUpdate(int staleRevision)
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var current = store.Save(first with { Name = "Committed change" }, null);
        var path = ProfilePath(root, current.Id);
        var before = File.ReadAllBytes(path);
        var backup = File.ReadAllBytes(path + ".bak");

        Assert.Throws<InvalidOperationException>(() => root.OpenStore().Save(
            first with { Revision = staleRevision, Name = "Stale change" }, Sas(credential, first.CredentialRevision)));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(current, root.OpenStore().Find(current.Id));
        AssertSecret(store, current.Id, credential);
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void NewProfileRequiresRevisionZeroAndACredential()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var profile = SyntheticCredential.Profile();
        var credential = SyntheticCredential.Create();
        Assert.Throws<InvalidOperationException>(() => store.Save(profile with { Revision = 1 }, Sas(credential)));
        Assert.Throws<ArgumentException>(() => store.Save(profile, null));
        Assert.Empty(store.List());
        Assert.False(File.Exists(ProfilePath(root, profile.Id)));
    }

    [Fact]
    public void RenewalChangesOnlyCredentialExpiryAndRevision()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var original = SyntheticCredential.Create();
        var replacement = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(original));
        store.Renew(first.Id, replacement.Url);
        var renewed = root.OpenStore().Find(first.Id);

        Assert.Equal(first with { CredentialRevision = 2, ExpiresAt = replacement.ExpiresAt }, renewed);
        AssertSecret(root.OpenStore(), first.Id, replacement);
        Assert.Equal(2, store.Save(first with { Name = "Metadata after renewal" }, null).Revision);
        root.AssertNoPlaintext([original, replacement]);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("container")]
    public void RenewalRejectsDifferentSourceWithoutMutatingDisk(string difference)
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var other = difference == "endpoint"
            ? SyntheticCredential.Create("https://ctdotherlocal000.blob.core.windows.net")
            : SyntheticCredential.Create(container: "different-container");
        var path = ProfilePath(root, profile.Id);
        var before = File.ReadAllBytes(path);

        Assert.Throws<ArgumentException>(() => store.Renew(profile.Id, other.Url));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.Equal(profile, store.Find(profile.Id));
        AssertSecret(store, profile.Id, credential);
        root.AssertNoPlaintext([credential, other]);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("container")]
    public void MetadataSaveCannotChangeSourceWithoutReplacementCredential(string difference)
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var changed = difference == "endpoint"
            ? profile with { Endpoint = "https://ctdotherlocal000.blob.core.windows.net" }
            : profile with { Container = "different-container" };
        Assert.Throws<ArgumentException>(() => store.Save(changed, null));
        Assert.Equal(profile, root.OpenStore().Find(profile.Id));
        AssertSecret(store, profile.Id, credential);
    }

    [Fact]
    public void RemovalTombstonesIdentityAndPreservesCacheRuntimeIntentAndEncryptedBackup()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var original = SyntheticCredential.Profile();
        var saved = store.Save(original, Sas(credential));
        var cache = store.CachePath(saved.Id);
        var runtime = store.RuntimePath(saved.Id);
        var cacheFile = Path.Combine(cache, "pending-synthetic-data.bin");
        var runtimeFile = Path.Combine(runtime, "synthetic-recovery.txt");
        byte[] pending = [0, 1, 2, 127, 255];
        File.WriteAllBytes(cacheFile, pending);
        File.WriteAllText(runtimeFile, "synthetic recovery evidence");
        store.WriteIntent(NewIntent(saved));
        var intentBytes = File.ReadAllBytes(IntentPath(root, saved.Id));
        var path = ProfilePath(root, saved.Id);
        var primary = File.ReadAllBytes(path);

        store.Remove(saved.Id);

        var reopened = root.OpenStore();
        Assert.Null(reopened.Find(saved.Id));
        Assert.Empty(reopened.List());
        Assert.Throws<KeyNotFoundException>(() => reopened.ReadSecret(saved.Id));
        Assert.Throws<KeyNotFoundException>(() => reopened.Renew(saved.Id, credential.Url));
        Assert.Throws<InvalidOperationException>(() => reopened.Save(original, Sas(credential)));
        Assert.Throws<InvalidOperationException>(() => reopened.Save(saved, Sas(credential, saved.CredentialRevision)));
        var tombstone = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(tombstone);
        Assert.True(document.RootElement.GetProperty("removed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("protectedCredential").ValueKind);
        Assert.Equal(saved.Revision + 1, document.RootElement.GetProperty("profile").GetProperty("revision").GetInt32());
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(pending, File.ReadAllBytes(cacheFile));
        Assert.Equal("synthetic recovery evidence", File.ReadAllText(runtimeFile));
        Assert.Equal(intentBytes, File.ReadAllBytes(IntentPath(root, saved.Id)));
        Assert.NotNull(reopened.ReadIntent(saved.Id));
        Assert.Equal(cache, reopened.CachePath(saved.Id));
        Assert.Equal(runtime, reopened.RuntimePath(saved.Id));
        reopened.Remove(saved.Id);
        Assert.Equal(tombstone, File.ReadAllBytes(path));
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void IntentUsesVersionedFormatAndReopeningPreservesIdentityAndRecoveryFlags()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var supplied = NewIntent(profile) with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var before = DateTimeOffset.UtcNow;
        store.WriteIntent(supplied);
        var after = DateTimeOffset.UtcNow;
        var observed = root.OpenStore().ReadIntent(profile.Id);
        Assert.NotNull(observed);
        Assert.Equal(supplied with { UpdatedAt = observed.UpdatedAt }, observed);
        Assert.InRange(observed.UpdatedAt, before, after);
        var path = IntentPath(root, profile.Id);
        var raw = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(observed, document.RootElement.GetProperty("intent").Deserialize<MountIntent>(Wire.Json));

        store.WriteIntent(observed with { Uncertain = false });
        Assert.Equal(raw, File.ReadAllBytes(path + ".bak"));
        Assert.False(root.OpenStore().ReadIntent(profile.Id)!.Uncertain);
        root.AssertNoPlaintext([credential]);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("container")]
    [InlineData("prefix")]
    public void IntentForADifferentSourceIdentityIsRejected(string difference)
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var other = difference switch
        {
            "endpoint" => profile with { Endpoint = "https://ctdotherlocal000.blob.core.windows.net" },
            "container" => profile with { Container = "other-container" },
            _ => profile with { Prefix = profile.Prefix.ToLowerInvariant() }
        };
        Assert.Throws<ArgumentException>(() => store.WriteIntent(NewIntent(other)));
        Assert.Null(store.ReadIntent(profile.Id));
        Assert.False(File.Exists(IntentPath(root, profile.Id)));
    }

    [Fact]
    public void OldIntentRemainsRecoveryEvidenceAfterPrefixIdentityChanges()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        store.WriteIntent(NewIntent(profile));
        var original = store.ReadIntent(profile.Id);
        var cache = store.CachePath(profile.Id);
        var changed = store.Save(profile with { Prefix = "new-prefix" }, null);

        Assert.Equal(original, root.OpenStore().ReadIntent(profile.Id));
        Assert.NotEqual(original!.Identity, Validation.Identity(changed));
        Assert.Equal(cache, store.CachePath(changed.Id));
    }

    [Theory]
    [InlineData("future-schema")]
    [InlineData("wrong-profile-id")]
    [InlineData("missing-field")]
    [InlineData("unknown-field")]
    [InlineData("null-identity")]
    [InlineData("control-character")]
    [InlineData("default-time")]
    [InlineData("duplicate-property")]
    [InlineData("truncated-json")]
    public void CorruptIntentIsRejectedAndCannotBeOverwritten(string corruption)
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var intent = NewIntent(profile);
        store.WriteIntent(intent);
        var path = IntentPath(root, profile.Id);
        var record = ReadRecord(path);
        var body = record["intent"]!.AsObject();
        switch (corruption)
        {
            case "future-schema": record["schemaVersion"] = 999; break;
            case "wrong-profile-id": body["profileId"] = Guid.NewGuid(); break;
            case "missing-field": body.Remove("wasWritable"); break;
            case "unknown-field": body["unrecognized"] = true; break;
            case "null-identity": body["identity"] = null; break;
            case "control-character": body["identity"] = "invalid\nidentity"; break;
            case "default-time": body["updatedAt"] = JsonValue.Create(default(DateTimeOffset)); break;
        }
        var json = record.ToJsonString();
        if (corruption == "duplicate-property") json = "{\"schemaVersion\":1," + json[1..];
        if (corruption == "truncated-json") json = json[..^1];
        File.WriteAllText(path, json);
        var bytes = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => root.OpenStore().ReadIntent(profile.Id));
        Assert.Throws<InvalidDataException>(() => store.WriteIntent(intent));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void MissingIntentPrimaryWithBackupCannotBeSilentlyRecreated()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = store.Save(SyntheticCredential.Profile(), Sas(credential));
        var intent = NewIntent(profile);
        store.WriteIntent(intent);
        store.WriteIntent(intent with { Uncertain = false });
        var path = IntentPath(root, profile.Id);
        var backup = File.ReadAllBytes(path + ".bak");
        File.Delete(path);

        Assert.Throws<InvalidDataException>(() => store.ReadIntent(profile.Id));
        Assert.Throws<InvalidDataException>(() => store.WriteIntent(intent));
        Assert.False(File.Exists(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void MissingProfilePrimaryWithBackupIsNotReportedAsAnEmptyStore()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(credential));
        store.Save(first with { Name = "Second revision" }, null);
        var path = ProfilePath(root, first.Id);
        var backup = File.ReadAllBytes(path + ".bak");
        File.Delete(path);

        Assert.Throws<InvalidDataException>(() => root.OpenStore().List());
        Assert.Throws<InvalidDataException>(() => store.Find(first.Id));
        Assert.Throws<InvalidDataException>(() => store.Save(first with { Revision = 0 }, Sas(credential)));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void AccountKeyRoundTripUsesKindBoundDpapiAndIndependentCredentialRevision()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var profile = SyntheticCredential.Profile() with { AuthenticationKind = AuthenticationKind.AccountKey };
        var saved = store.Save(profile, CredentialSubmission.AccountKeyCredential(key));

        Assert.Equal(1, saved.Revision);
        Assert.Equal(1, saved.CredentialRevision);
        Assert.Equal(AuthenticationKind.AccountKey, saved.AuthenticationKind);
        Assert.Null(saved.ExpiresAt);
        var recovered = root.OpenStore().ReadCredential(saved.Id);
        Assert.Equal(AuthenticationKind.AccountKey, recovered.Kind);
        Assert.Equal(saved.CredentialRevision, recovered.ExpectedRevision);
        Assert.True(string.Equals(key, recovered.AccountKey, StringComparison.Ordinal), "The synthetic account key differs; key text is not printed.");
        var raw = File.ReadAllBytes(ProfilePath(root, saved.Id));
        Assert.True(raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(key)) < 0, "A profile record contains a synthetic account key in plaintext.");
    }

    [Fact]
    public void SameSourceAuthenticationSwitchPreservesCacheIdentityAndRejectsStaleCredentialRevision()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var sas = SyntheticCredential.Create();
        var first = store.Save(SyntheticCredential.Profile(), Sas(sas));
        var cache = store.CachePath(first.Id);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var switched = store.Save(first with { AuthenticationKind = AuthenticationKind.AccountKey },
            CredentialSubmission.AccountKeyCredential(key, first.CredentialRevision));

        Assert.Equal(first.Revision + 1, switched.Revision);
        Assert.Equal(first.CredentialRevision + 1, switched.CredentialRevision);
        Assert.Equal(AuthenticationKind.AccountKey, switched.AuthenticationKind);
        Assert.Equal(cache, store.CachePath(switched.Id));
        Assert.Equal(Validation.Identity(first), Validation.Identity(switched));
        Assert.Throws<InvalidOperationException>(() => store.Renew(switched.Id,
            CredentialSubmission.AccountKeyCredential(key, first.CredentialRevision)));
    }

    [Fact]
    public void LegacySchemaTwoCredentialMigratesToNewEntropyWithImmutableBackup()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = SyntheticCredential.Profile() with
        {
            Revision = 1,
            CredentialRevision = 1,
            ExpiresAt = credential.ExpiresAt
        };
        var plaintext = Encoding.UTF8.GetBytes(credential.Url);
        byte[] protectedCredential;
        try
        {
            protectedCredential = ProtectedData.Protect(plaintext,
                Encoding.UTF8.GetBytes("BlobToDrive/credential/v2/ContainerSas/" + profile.Id.ToString("N")),
                DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var record = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["profile"] = JsonSerializer.SerializeToNode(profile, Wire.Json),
            ["protectedCredential"] = Convert.ToBase64String(protectedCredential),
            ["removed"] = false
        };
        var path = ProfilePath(root, profile.Id);
        var legacyBytes = Encoding.UTF8.GetBytes(record.ToJsonString());
        File.WriteAllBytes(path, legacyBytes);
        AppPaths.SecureFile(path);

        var migrated = store.Find(profile.Id);

        Assert.Equal(profile, migrated);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllBytes(path))!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(legacyBytes, File.ReadAllBytes(path + ".v2.bak"));
        Assert.Equal(legacyBytes, File.ReadAllBytes(path + ".bak"));
        AssertSecret(root.OpenStore(), profile.Id, credential);
        root.AssertNoPlaintext([credential]);
    }

    [Fact]
    public void LegacySchemaOneSasRecordMigratesAtomicallyAndRetainsImmutableCiphertextBackup()
    {
        var root = LocalTestRoot.Create();
        var store = root.OpenStore();
        var credential = SyntheticCredential.Create();
        var profile = SyntheticCredential.Profile() with { Revision = 1, ExpiresAt = credential.ExpiresAt };
        var plaintext = Encoding.UTF8.GetBytes(credential.Url);
        byte[] protectedSas;
        try
        {
            protectedSas = ProtectedData.Protect(plaintext,
                Encoding.UTF8.GetBytes("BlobToDrive/credential/v1/" + profile.Id.ToString("N")), DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var profileNode = JsonSerializer.SerializeToNode(profile, Wire.Json)!.AsObject();
        foreach (var name in new[] { "authenticationKind", "credentialRevision", "tenantId", "subscriptionId", "resourceGroupName" })
            profileNode.Remove(name);
        var record = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["profile"] = profileNode,
            ["protectedSas"] = Convert.ToBase64String(protectedSas),
            ["removed"] = false
        };
        var path = ProfilePath(root, profile.Id);
        var legacyBytes = Encoding.UTF8.GetBytes(record.ToJsonString());
        File.WriteAllBytes(path, legacyBytes);
        AppPaths.SecureFile(path);

        var migrated = store.Find(profile.Id);

        Assert.NotNull(migrated);
        Assert.Equal(AuthenticationKind.ContainerSas, migrated.AuthenticationKind);
        Assert.Equal(1, migrated.CredentialRevision);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllBytes(path))!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(legacyBytes, File.ReadAllBytes(path + ".v1.bak"));
        Assert.Equal(legacyBytes, File.ReadAllBytes(path + ".bak"));
        AssertSecret(root.OpenStore(), profile.Id, credential);
        root.AssertNoPlaintext([credential]);
    }

    private static void AssertSecret(ProfileStore store, Guid id, SyntheticCredential credential)
    {
        // Assert.Equal would print the credential on failure; deliberately assert only the comparison result.
        var secretMatches = string.Equals(credential.Url, store.ReadSecret(id), StringComparison.Ordinal);
        Assert.True(secretMatches,
            "The recovered synthetic credential differs; credential values are intentionally not printed.");
    }

    private static string ProfilePath(LocalTestRoot root, Guid id) => root.PathFor("profiles", id.ToString("N") + ".json");
    private static string IntentPath(LocalTestRoot root, Guid id) => root.PathFor("intents", id.ToString("N") + ".json");
    private static JsonObject ReadRecord(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    private static CredentialSubmission Sas(SyntheticCredential credential, int expectedRevision = 0) =>
        CredentialSubmission.ContainerSas(credential.Url, expectedRevision);
    private static MountIntent NewIntent(Profile profile) => new()
    {
        ProfileId = profile.Id,
        Identity = Validation.Identity(profile),
        EngineVersion = Rclone.EngineLocator.Version,
        WasWritable = true,
        Uncertain = true
    };
}