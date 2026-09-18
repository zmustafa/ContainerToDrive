using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ContainerToDrive.Core;

namespace ContainerToDrive.Windows;

/// <summary>
/// Synchronous, briefly locked persistence. The controller owns mount/recovery and identity-migration policy.
/// Metadata and DPAPI ciphertext are one atomic record; exporting this directory is not a profile export.
/// </summary>
public sealed class ProfileStore
{
    private const int ProfileSchemaVersion = 3;
    private const int LegacyProfileSchemaVersion = 2;
    private const int IntentSchemaVersion = 1;
    private const int MaxRecordBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private readonly string _profiles;
    private readonly string _intents;
    private readonly string _cache;
    private readonly string _runtime;

    public ProfileStore(string dataRoot)
    {
        DataRoot = AppPaths.NormalizeDataRoot(dataRoot);
        _profiles = Path.Combine(DataRoot, "profiles");
        _intents = Path.Combine(DataRoot, "intents");
        _cache = Path.Combine(DataRoot, "cache");
        _runtime = Path.Combine(DataRoot, "runtime");
        foreach (var directory in new[] { DataRoot, _profiles, _intents, _cache, _runtime })
            AppPaths.SecureDirectory(directory);
    }

    public string DataRoot { get; }

    public List<Profile> List() => Locked(() =>
    {
        var profiles = new List<Profile>();
        // A missing primary must not silently hide recovery evidence from the controller's snapshot.
        foreach (var backup in Directory.EnumerateFiles(_profiles, "*.json.bak", SearchOption.TopDirectoryOnly))
            if (!AppPaths.FileExists(backup[..^4])) throw Corrupt();
        foreach (var path in Directory.EnumerateFiles(_profiles, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || id == Guid.Empty)
                throw Corrupt();
            var stored = Load(id) ?? throw Corrupt();
            if (!stored.Removed) profiles.Add(stored.Profile);
        }
        return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ThenBy(profile => profile.Id).ToList();
    });

    public Profile? Find(Guid id) => Locked(() =>
    {
        var stored = Load(id);
        return stored is null || stored.Removed ? null : stored.Profile;
    });

    public Profile Save(Profile profile, CredentialSubmission? credential)
    {
        Validate(profile);
        return Locked(() => SaveCore(profile, credential));
    }

    public CredentialSubmission ReadCredential(Guid id) => Locked(() =>
    {
        var stored = RequireProfile(id);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(stored.ProtectedCredential!, Entropy(id, stored.Profile.AuthenticationKind), DataProtectionScope.CurrentUser);
            if (plaintext.Length is < 1 or > 65536) throw new CryptographicException();
            // Expiry is intentionally not checked on decrypt: the controller must surface expired credentials.
            var secret = Utf8.GetString(plaintext);
            return stored.Profile.AuthenticationKind switch
            {
                AuthenticationKind.ContainerSas => CredentialSubmission.ContainerSas(secret, stored.Profile.CredentialRevision),
                AuthenticationKind.AccountKey => CredentialSubmission.AccountKeyCredential(secret, stored.Profile.CredentialRevision),
                AuthenticationKind.MicrosoftEntra => CredentialSubmission.MicrosoftEntraSas(secret, stored.Profile.CredentialRevision),
                _ => throw new CryptographicException()
            };
        }
        catch (Exception exception) when (exception is CryptographicException or DecoderFallbackException)
        {
            throw new CryptographicException("The saved credential could not be decrypted for this Windows user.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    });

    public string ReadSecret(Guid id)
    {
        var credential = ReadCredential(id);
        return credential.SasUrl ?? credential.AccountKey ?? throw new CryptographicException("The saved credential could not be decrypted for this Windows user.");
    }

    public Profile Renew(Guid id, CredentialSubmission credential) => Locked(() =>
    {
        var stored = RequireProfile(id);
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Kind != stored.Profile.AuthenticationKind)
            throw new ArgumentException("Use profile editing to change authentication method while disconnected.");
        if (credential.ExpectedRevision != stored.Profile.CredentialRevision)
            throw new InvalidOperationException("The credential changed. Refresh it before renewing again.");
        var info = Validation.ValidateCredential(stored.Profile, credential);
        var protectedCredential = Protect(info.Secret, id, credential.Kind);
        var renewed = stored.Profile with
        {
            CredentialRevision = NextRevision(stored.Profile.CredentialRevision),
            ExpiresAt = info.ExpiresAt
        };
        AtomicWrite(ProfilePath(id), new StoredProfile
        {
            SchemaVersion = ProfileSchemaVersion,
            Profile = renewed,
            ProtectedCredential = protectedCredential,
            Removed = false
        });
        return renewed;
    });

    public void Renew(Guid id, string sas)
    {
        var profile = Find(id) ?? throw new KeyNotFoundException("The profile does not exist.");
        Renew(id, CredentialSubmission.ContainerSas(sas, profile.CredentialRevision));
    }

    public void Remove(Guid id) => Locked(() =>
    {
        var stored = Load(id);
        if (stored is not null && !stored.Removed)
        {
            // A tombstone prevents stale revision-zero requests from resurrecting a retained cache ID.
            // Keep cache/intent/runtime and the encrypted backup for deliberate controller-led recovery.
            AtomicWrite(ProfilePath(id), stored with
            {
                Profile = stored.Profile with { Revision = NextRevision(stored.Profile.Revision) },
                ProtectedCredential = null,
                Removed = true
            });
        }
        return true;
    });

    public void WriteIntent(MountIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        Locked(() =>
        {
            var profile = RequireProfile(intent.ProfileId).Profile;
            ValidateIntent(intent);
            if (intent.Identity != Validation.Identity(profile))
                throw new ArgumentException("The mount intent does not match the saved profile identity.");
            // Do not overwrite corrupt or future-version recovery evidence with today's schema.
            ReadIntentCore(intent.ProfileId);
            AtomicWrite(IntentPath(intent.ProfileId), new StoredIntent
            {
                SchemaVersion = IntentSchemaVersion,
                Intent = intent with { UpdatedAt = DateTimeOffset.UtcNow }
            });
            return true;
        });
    }

    public MountIntent? ReadIntent(Guid id) => Locked(() => ReadIntentCore(id));

    public string CachePath(Guid id) => ProtectedChild(_cache, id);
    public string RuntimePath(Guid id) => ProtectedChild(_runtime, id);

    private MountIntent? ReadIntentCore(Guid id)
    {
        var path = IntentPath(id);
        if (!AppPaths.FileExists(path))
        {
            RequireNoOrphanedBackup(path);
            return null;
        }
        var record = ReadJson<StoredIntent>(path);
        if (record.SchemaVersion != IntentSchemaVersion || record.Intent is null || record.Intent.ProfileId != id)
            throw Corrupt();
        try { ValidateIntent(record.Intent); }
        catch (ArgumentException) { throw Corrupt(); }
        // Do not compare against today's profile: an older intent is evidence needed for migration/recovery gates.
        return record.Intent;
    }

    private Profile SaveCore(Profile profile, CredentialSubmission? credential)
    {
        var existing = Load(profile.Id);
        if (existing?.Removed == true || (existing is null ? profile.Revision != 0 : profile.Revision != existing.Profile.Revision))
            throw new InvalidOperationException("The profile changed. Refresh it before saving again.");

        byte[] protectedCredential;
        DateTimeOffset? expiry;
        int credentialRevision;
        if (credential is not null)
        {
            var expected = existing?.Profile.CredentialRevision ?? 0;
            if (credential.ExpectedRevision != expected)
                throw new InvalidOperationException("The credential changed. Refresh it before saving again.");
            var info = Validation.ValidateCredential(profile, credential);
            expiry = info.ExpiresAt;
            protectedCredential = Protect(info.Secret, profile.Id, credential.Kind);
            credentialRevision = NextRevision(expected);
        }
        else
        {
            if (existing is null) throw new ArgumentException("A new profile requires a container SAS credential.");
            if (!SameSource(profile, existing.Profile.Endpoint, existing.Profile.Container))
                throw new ArgumentException("Changing a profile's source requires a matching replacement credential.");
            if (profile.AuthenticationKind != existing.Profile.AuthenticationKind ||
                profile.TenantId != existing.Profile.TenantId || profile.SubscriptionId != existing.Profile.SubscriptionId ||
                profile.ResourceGroupName != existing.Profile.ResourceGroupName)
                throw new ArgumentException("Changing authentication method requires a matching replacement credential.");
            protectedCredential = existing.ProtectedCredential!;
            expiry = existing.Profile.ExpiresAt;
            credentialRevision = existing.Profile.CredentialRevision;
        }

        var saved = profile with
        {
            Revision = NextRevision(profile.Revision),
            CredentialRevision = credentialRevision,
            ExpiresAt = expiry
        };
        AtomicWrite(ProfilePath(saved.Id), new StoredProfile
        {
            SchemaVersion = ProfileSchemaVersion,
            Profile = saved,
            ProtectedCredential = protectedCredential,
            Removed = false
        });
        return saved;
    }

    private StoredProfile? Load(Guid id)
    {
        var path = ProfilePath(id);
        if (!AppPaths.FileExists(path))
        {
            RequireNoOrphanedBackup(path);
            return null;
        }
        var bytes = ReadBytes(path);
        using var document = ParseDocument(bytes);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number)
            throw Corrupt();
        var stored = schema.GetInt32() switch
        {
            ProfileSchemaVersion => Deserialize<StoredProfile>(bytes, Json),
            LegacyProfileSchemaVersion => MigrateV2(path, id, bytes),
            1 => MigrateV1(path, id, bytes),
            _ => throw Corrupt()
        };
        if (stored.Profile is null || stored.Profile.Id != id || stored.Profile.Revision < 1 || stored.Profile.CredentialRevision < 1 ||
            stored.SchemaVersion != ProfileSchemaVersion ||
            (stored.Removed ? stored.ProtectedCredential is not null : stored.ProtectedCredential is not { Length: > 0 and <= 131072 }))
            throw Corrupt();
        try { Validate(stored.Profile); }
        catch (ArgumentException) { throw Corrupt(); }
        return stored;
    }

    private StoredProfile RequireProfile(Guid id)
    {
        var stored = Load(id);
        if (stored is null || stored.Removed) throw new KeyNotFoundException("The profile does not exist.");
        return stored;
    }

    private T Locked<T>(Func<T> operation)
    {
        AppPaths.RejectReparsePoints(DataRoot);
        var lockPath = Path.Combine(DataRoot, "store.lock");
        var timer = Stopwatch.StartNew();
        FileStream? held = null;
        while (held is null)
        {
            AppPaths.RejectReparsePoints(lockPath);
            try
            {
                // Never delete this lock file: deleting/recreating it could split the lock domain.
                held = new FileInfo(lockPath).Create(FileMode.OpenOrCreate, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.None, AppPaths.FileAcl());
            }
            catch (IOException exception) when (AppPaths.IsSharingViolation(exception))
            {
                if (timer.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("The profile store is busy. Try again.");
                Thread.Sleep(25);
            }
        }
        using (held)
        {
            // Operate on the open file handle: a second path-based open conflicts with FileShare.None.
            var owner = held.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            if (owner?.Value != AppPaths.CurrentSid && owner?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("The profile store lock has an unexpected owner.");
            held.SetAccessControl(AppPaths.FileAcl());
            AppPaths.RejectReparsePoints(_profiles);
            AppPaths.RejectReparsePoints(_intents);
            return operation();
        }
    }

    private static T ReadJson<T>(string path)
    {
        var bytes = ReadBytes(path);
        using var document = ParseDocument(bytes);
        return Deserialize<T>(bytes, Json);
    }

    private static byte[] ReadBytes(string path)
    {
        AppPaths.SecureFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaxRecordBytes) throw Corrupt();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonDocument ParseDocument(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { RejectDuplicateProperties(document.RootElement); }
            catch { document.Dispose(); throw; }
            return document;
        }
        catch (JsonException) { throw Corrupt(); }
    }

    private static T Deserialize<T>(byte[] bytes, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, options) ?? throw Corrupt(); }
        catch (JsonException) { throw Corrupt(); }
    }

    private static StoredProfile MigrateV2(string path, Guid id, byte[] bytes)
    {
        var legacy = Deserialize<StoredProfile>(bytes, Json);
        if (legacy.SchemaVersion != LegacyProfileSchemaVersion || legacy.Profile is null || legacy.Profile.Id != id ||
            legacy.Profile.Revision < 1 || legacy.Profile.CredentialRevision < 1 ||
            (legacy.Removed ? legacy.ProtectedCredential is not null : legacy.ProtectedCredential is not { Length: > 0 and <= 131072 }))
            throw Corrupt();
        try { Validate(legacy.Profile); }
        catch (ArgumentException) { throw Corrupt(); }

        byte[]? protectedCredential = null;
        byte[]? plaintext = null;
        try
        {
            if (!legacy.Removed)
            {
                plaintext = ProtectedData.Unprotect(legacy.ProtectedCredential!,
                    LegacyV2Entropy(id, legacy.Profile.AuthenticationKind), DataProtectionScope.CurrentUser);
                if (plaintext.Length is < 1 or > 65536) throw new CryptographicException();
                protectedCredential = ProtectedData.Protect(plaintext,
                    Entropy(id, legacy.Profile.AuthenticationKind), DataProtectionScope.CurrentUser);
            }
        }
        catch (CryptographicException) { throw Corrupt(); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }

        WriteLegacyBackup(path + ".v2.bak", bytes);
        var migrated = new StoredProfile
        {
            SchemaVersion = ProfileSchemaVersion,
            Profile = legacy.Profile,
            ProtectedCredential = protectedCredential,
            Removed = legacy.Removed
        };
        AtomicWrite(path, migrated);
        return migrated;
    }

    private static StoredProfile MigrateV1(string path, Guid id, byte[] bytes)
    {
        var legacy = Deserialize<LegacyStoredProfile>(bytes, LegacyJson);
        if (legacy.SchemaVersion != 1 || legacy.Profile is null || legacy.Profile.Id != id || legacy.Profile.Revision < 1 ||
            (legacy.Removed ? legacy.ProtectedSas is not null : legacy.ProtectedSas is not { Length: > 0 and <= 131072 }))
            throw Corrupt();

        var profile = legacy.Profile with
        {
            AuthenticationKind = AuthenticationKind.ContainerSas,
            CredentialRevision = 1,
            TenantId = "",
            SubscriptionId = "",
            ResourceGroupName = ""
        };
        try { Validate(profile); }
        catch (ArgumentException) { throw Corrupt(); }

        byte[]? protectedCredential = null;
        byte[]? plaintext = null;
        try
        {
            if (!legacy.Removed)
            {
                plaintext = ProtectedData.Unprotect(legacy.ProtectedSas!, LegacyEntropy(id), DataProtectionScope.CurrentUser);
                if (plaintext.Length is < 1 or > 65536) throw new CryptographicException();
                var sas = Utf8.GetString(plaintext);
                var validationTime = profile.ExpiresAt?.AddTicks(-1) ?? DateTimeOffset.UtcNow;
                var info = Validation.ParseSas(sas, validationTime);
                if (!SameSource(profile, info.Endpoint, info.Container)) throw Corrupt();
                profile = profile with { ExpiresAt = info.ExpiresAt };
                protectedCredential = Protect(sas, id, AuthenticationKind.ContainerSas);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or DecoderFallbackException or ArgumentException)
        {
            throw Corrupt();
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }

        WriteLegacyBackup(path + ".v1.bak", bytes);
        var migrated = new StoredProfile
        {
            SchemaVersion = ProfileSchemaVersion,
            Profile = profile,
            ProtectedCredential = protectedCredential,
            Removed = legacy.Removed
        };
        AtomicWrite(path, migrated);
        return migrated;
    }

    private static void WriteLegacyBackup(string path, byte[] bytes)
    {
        AppPaths.RejectReparsePoints(path);
        if (AppPaths.FileExists(path))
        {
            AppPaths.SecureFile(path);
            if (!CryptographicOperations.FixedTimeEquals(ReadBytes(path), bytes)) throw Corrupt();
            return;
        }
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.Read, 4096, FileOptions.WriteThrough, AppPaths.FileAcl());
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] Protect(string secret, Guid id, AuthenticationKind kind)
    {
        var plaintext = Utf8.GetBytes(secret);
        try { return ProtectedData.Protect(plaintext, Entropy(id, kind), DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new CryptographicException("The credential could not be protected for this Windows user."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void AtomicWrite<T>(string path, T record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("The application record is too large.");
        AppPaths.RejectReparsePoints(path);
        var exists = AppPaths.FileExists(path);
        if (exists) AppPaths.SecureFile(path);
        var backup = path + ".bak";
        if (AppPaths.FileExists(backup)) AppPaths.SecureFile(backup);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.WriteThrough, AppPaths.FileAcl()))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            AppPaths.RejectReparsePoints(path);
            AppPaths.RejectReparsePoints(backup);
            if (exists) File.Replace(temporary, path, backup, ignoreMetadataErrors: false);
            else File.Move(temporary, path);
        }
        finally
        {
            // A leftover temp contains metadata/ciphertext only, never a plaintext SAS.
            // Cleanup failure must not turn a committed operation into a reported failure/retry.
            try
            {
                if (AppPaths.FileExists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RequireNoOrphanedBackup(string path)
    {
        if (AppPaths.FileExists(path + ".bak")) throw Corrupt();
    }

    private static string ProtectedChild(string parent, Guid id)
    {
        ValidateId(id);
        var path = Path.Combine(parent, id.ToString("N"));
        AppPaths.SecureDirectory(path);
        return path;
    }

    private string ProfilePath(Guid id) { ValidateId(id); return Path.Combine(_profiles, id.ToString("N") + ".json"); }
    private string IntentPath(Guid id) { ValidateId(id); return Path.Combine(_intents, id.ToString("N") + ".json"); }
    private static byte[] LegacyEntropy(Guid id) => Encoding.UTF8.GetBytes("BlobToDrive/credential/v1/" + id.ToString("N"));
    private static byte[] LegacyV2Entropy(Guid id, AuthenticationKind kind) =>
        Encoding.UTF8.GetBytes("BlobToDrive/credential/v2/" + kind + "/" + id.ToString("N"));
    private static byte[] Entropy(Guid id, AuthenticationKind kind) =>
        Encoding.UTF8.GetBytes("ContainerToDrive/credential/v3/" + kind + "/" + id.ToString("N"));
    private static int NextRevision(int revision) => revision < int.MaxValue ? revision + 1 : throw new InvalidOperationException("The profile revision limit was reached.");
    private static bool SameSource(Profile profile, string endpoint, string container) =>
        string.Equals(profile.Endpoint.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && profile.Container == container;

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A profile ID is required.");
    }

    private static void Validate(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Name is null || profile.Endpoint is null || profile.Container is null || profile.Prefix is null ||
            profile.DriveLetter is null || profile.TenantId is null || profile.SubscriptionId is null ||
            profile.ResourceGroupName is null || profile.Revision < 0 || profile.CredentialRevision < 0)
            throw new ArgumentException("The profile contains missing or invalid fields.");
        Validation.ValidateProfile(profile);
    }

    private static void ValidateIntent(MountIntent intent)
    {
        ValidateId(intent.ProfileId);
        if (string.IsNullOrWhiteSpace(intent.Identity) || intent.Identity.Length > 2048 || intent.Identity.Any(char.IsControl) ||
            intent.EngineVersion is null || intent.EngineVersion.Length > 100 || intent.EngineVersion.Any(char.IsControl) ||
            intent.UpdatedAt == default)
            throw new ArgumentException("The mount intent contains missing or invalid fields.");
    }

    private static InvalidDataException Corrupt() => new("Saved application state is unreadable or unsupported. Preserve it and its cache for repair.");

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Corrupt();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(Profile) && type.Type != typeof(MountIntent)) return;
            foreach (var property in type.Properties)
                if (property.Set is not null) property.IsRequired = true;
        });
        return new JsonSerializerOptions(Wire.Json)
        {
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
            TypeInfoResolver = resolver
        };
    }

    private static JsonSerializerOptions CreateLegacyJsonOptions() => new(Wire.Json)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private static readonly JsonSerializerOptions LegacyJson = CreateLegacyJsonOptions();

    private sealed record StoredProfile
    {
        public required int SchemaVersion { get; init; }
        public required Profile Profile { get; init; }
        public required byte[]? ProtectedCredential { get; init; }
        public required bool Removed { get; init; }
    }

    private sealed record LegacyStoredProfile
    {
        public required int SchemaVersion { get; init; }
        public required Profile Profile { get; init; }
        public required byte[]? ProtectedSas { get; init; }
        public required bool Removed { get; init; }
    }

    private sealed record StoredIntent
    {
        public required int SchemaVersion { get; init; }
        public required MountIntent Intent { get; init; }
    }
}