using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ContainerToDrive.Core;

public sealed record SasInfo(string Endpoint, string Container, DateTimeOffset? ExpiresAt, string? Permissions)
{
    public bool IsUserDelegation { get; init; }
}

public sealed record CredentialInfo(AuthenticationKind Kind, string Secret, DateTimeOffset? ExpiresAt);

public static partial class Validation
{
    public static SasInfo ParseSas(string value, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !AzureHost().IsMatch(uri.Host))
            throw new ArgumentException("Enter an HTTPS container SAS URL for Azure public-cloud Blob Storage.");
        var path = uri.AbsolutePath.Trim('/');
        if (!ContainerName().IsMatch(path) || path.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("Use a container SAS URL, not an account URL, single blob, or directory URL.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            if (pieces.Length != 2 || !query.TryAdd(key, Uri.UnescapeDataString(pieces[1])))
                throw new ArgumentException("The SAS query contains missing or duplicate fields.");
        }
        if (query.GetValueOrDefault("sr") != "c" || string.IsNullOrWhiteSpace(query.GetValueOrDefault("sig")) || query.ContainsKey("ss"))
            throw new ArgumentException("A signed container-scoped SAS is required.");
        if (query.TryGetValue("spr", out var protocol) && protocol != "https")
            throw new ArgumentException("Use an HTTPS-only SAS.");
        var time = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset? expiry = null;
        if (query.TryGetValue("se", out var expires))
        {
            if (!DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                throw new ArgumentException("The SAS expiry is invalid.");
            expiry = parsed;
            if (parsed <= time) throw new ArgumentException("The SAS has expired. Supply a replacement for the same container.");
        }
        if (query.TryGetValue("st", out var starts))
        {
            if (!DateTimeOffset.TryParse(starts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
                throw new ArgumentException("The SAS start time is invalid.");
            if (start > time.AddMinutes(5))
                throw new ArgumentException("The SAS is not valid yet. Check its start time and the computer clock.");
        }
        var permissions = query.GetValueOrDefault("sp");
        if (permissions is not null && (!permissions.Contains('r') || !permissions.Contains('l')))
            throw new ArgumentException("Browsing requires both read and list permissions.");
        var delegationFields = new[] { "skoid", "sktid", "skt", "ske", "sks", "skv" };
        var delegationCount = delegationFields.Count(query.ContainsKey);
        if (delegationCount == delegationFields.Length)
        {
            if (!Guid.TryParse(query["skoid"], out var objectId) || objectId == Guid.Empty ||
                !Guid.TryParse(query["sktid"], out var tenantId) || tenantId == Guid.Empty ||
                query["sks"] != "b" || string.IsNullOrWhiteSpace(query["skv"]) ||
                !DateTimeOffset.TryParse(query["skt"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var keyStarts) ||
                !DateTimeOffset.TryParse(query["ske"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var keyExpires) ||
                keyExpires <= keyStarts || keyExpires - keyStarts > TimeSpan.FromDays(7) || expiry is not null && expiry > keyExpires)
                throw new ArgumentException("The user delegation SAS identity or lifetime is invalid.");
        }
        return new(uri.GetLeftPart(UriPartial.Authority), path, expiry, permissions)
        {
            IsUserDelegation = delegationCount == delegationFields.Length
        };
    }

    public static CredentialInfo ValidateCredential(Profile profile, CredentialSubmission credential, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateProfile(profile);
        ValidateCredentialShape(credential);
        if (credential.Kind != profile.AuthenticationKind)
            throw new ArgumentException("The credential type or revision does not match the profile.");

        switch (credential.Kind)
        {
            case AuthenticationKind.ContainerSas:
            {
                if (credential.AccountKey is not null || credential.SasUrl is null)
                    throw new ArgumentException("Provide exactly one container SAS credential.");
                var info = ParseSas(credential.SasUrl, now);
                RequireSameSource(profile, info);
                if (!profile.ReadOnly) RequireWritableSas(info);
                return new(credential.Kind, credential.SasUrl, info.ExpiresAt);
            }
            case AuthenticationKind.AccountKey:
            {
                if (credential.SasUrl is not null || credential.AccountKey is null)
                    throw new ArgumentException("Provide exactly one storage account key credential.");
                ValidateAccountKey(credential.AccountKey);
                return new(credential.Kind, credential.AccountKey, null);
            }
            case AuthenticationKind.MicrosoftEntra:
            {
                if (credential.AccountKey is not null || credential.SasUrl is null)
                    throw new ArgumentException("Provide exactly one Microsoft Entra delegation credential.");
                var info = ParseSas(credential.SasUrl, now);
                RequireSameSource(profile, info);
                if (!info.IsUserDelegation)
                    throw new ArgumentException("Microsoft Entra profiles require a user delegation SAS.");
                if (profile.ReadOnly && (info.Permissions is null || info.Permissions.Any(permission => permission is not ('r' or 'l'))))
                    throw new ArgumentException("A read-only Microsoft Entra profile requires a read/list-only delegation SAS.");
                if (!profile.ReadOnly) RequireWritableSas(info);
                return new(credential.Kind, credential.SasUrl, info.ExpiresAt);
            }
            default:
                throw new ArgumentException("Unsupported authentication method.");
        }
    }

    private static void RequireWritableSas(SasInfo info)
    {
        const string required = "rcwdl";
        if (info.Permissions is null || required.Any(permission => !info.Permissions.Contains(permission)))
            throw new ArgumentException("A writable profile requires SAS read, list, create, write, and delete permissions.");
    }

    public static void ValidateCredentialShape(CredentialSubmission credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!Enum.IsDefined(credential.Kind) || credential.ExpectedRevision < 0)
            throw new ArgumentException("The credential type or revision is invalid.");
        if (credential.Kind == AuthenticationKind.AccountKey)
        {
            if (credential.SasUrl is not null || credential.AccountKey is null)
                throw new ArgumentException("Provide exactly one storage account key credential.");
        }
        else if (credential.AccountKey is not null || credential.SasUrl is null)
            throw new ArgumentException("Provide exactly one SAS-based credential.");
    }

    public static Profile ValidateProfile(Profile profile)
    {
        if (profile.Id == Guid.Empty || profile.Name.Length is < 1 or > 100 || profile.Name.Any(char.IsControl))
            throw new ArgumentException("Enter a profile name between 1 and 100 characters.");
        if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
            !AzureHost().IsMatch(endpoint.Host) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/")
            throw new ArgumentException("Unsupported storage endpoint.");
        if (!ContainerName().IsMatch(profile.Container) || profile.Container.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("Invalid container name.");
        if (profile.DriveLetter.Length != 1 || profile.DriveLetter[0] < 'D' || profile.DriveLetter[0] > 'Z')
            throw new ArgumentException("Choose a drive letter between D and Z.");
        if (profile.Prefix.Length > 1024 || profile.Prefix.Any(char.IsControl) || profile.Prefix.Contains('\\') || profile.Prefix.Contains(':') ||
            profile.Prefix.StartsWith('/') || profile.Prefix.EndsWith('/') || profile.Prefix.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Use a relative blob prefix with forward slashes and no dot segments.");
        if (profile.CacheMaxBytes < 256L * 1024 * 1024 || profile.CacheMaxBytes > 1024L * 1024 * 1024 * 1024 || profile.MinFreeBytes < 1024L * 1024 * 1024)
            throw new ArgumentException("Cache target must be 256 MiB to 1 TiB; reserve at least 1 GiB free space.");
        if (!Enum.IsDefined(profile.AuthenticationKind) || profile.CredentialRevision < 0)
            throw new ArgumentException("Unsupported authentication method or credential revision.");
        if (profile.AuthenticationKind == AuthenticationKind.MicrosoftEntra)
        {
            if (!Guid.TryParse(profile.TenantId, out var tenant) || tenant == Guid.Empty ||
                !Guid.TryParse(profile.SubscriptionId, out var subscription) || subscription == Guid.Empty ||
                !ResourceGroupName().IsMatch(profile.ResourceGroupName) || profile.ResourceGroupName.EndsWith('.'))
                throw new ArgumentException("Microsoft Entra resource identity is incomplete or invalid.");
        }
        else if (profile.TenantId.Length != 0 || profile.SubscriptionId.Length != 0 || profile.ResourceGroupName.Length != 0)
            throw new ArgumentException("Azure management identity is allowed only for Microsoft Entra profiles.");
        return profile;
    }

    public static string AccountName(Profile profile)
    {
        ValidateProfile(profile);
        return new Uri(profile.Endpoint).Host.Split('.')[0];
    }

    public static string ValidateAccountName(string? accountName)
    {
        if (accountName is null || !AzureAccountName().IsMatch(accountName))
            throw new ArgumentException("Enter a valid lowercase Azure Storage account name.");
        return accountName;
    }

    public static string ValidateContainerName(string? containerName)
    {
        if (containerName is null || !ContainerName().IsMatch(containerName) || containerName.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("Invalid container name.");
        return containerName;
    }

    public static string EndpointForAccount(string accountName) =>
        "https://" + ValidateAccountName(accountName) + ".blob.core.windows.net";

    public static void ValidateAccountKey(string? accountKey)
    {
        if (accountKey is null || accountKey.Length is < 1 or > 1024 || accountKey.Any(char.IsControl))
            throw new ArgumentException("The storage account key has an unsupported format.");
        Span<byte> decoded = stackalloc byte[128];
        if (!Convert.TryFromBase64String(accountKey, decoded, out var length) || length != 64)
            throw new ArgumentException("The storage account key has an unsupported format.");
        CryptographicOperations.ZeroMemory(decoded);
    }

    public static string Identity(Profile profile) => profile.Endpoint.ToLowerInvariant().TrimEnd('/') + "/" + profile.Container + "/" + profile.Prefix;
    public static bool Overlaps(Profile a, Profile b) =>
        string.Equals(a.Endpoint.TrimEnd('/'), b.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && a.Container == b.Container &&
        (a.Prefix == b.Prefix || a.Prefix.Length == 0 || b.Prefix.Length == 0 || a.Prefix.StartsWith(b.Prefix + "/", StringComparison.Ordinal) || b.Prefix.StartsWith(a.Prefix + "/", StringComparison.Ordinal));

    [GeneratedRegex(@"^[a-z0-9]{3,24}\.blob\.core\.windows\.net$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureHost();
    [GeneratedRegex(@"^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureAccountName();
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{1,61})[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerName();
    [GeneratedRegex(@"^[a-zA-Z0-9_().-]{1,90}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceGroupName();

    private static void RequireSameSource(Profile profile, SasInfo info)
    {
        if (!string.Equals(profile.Endpoint.TrimEnd('/'), info.Endpoint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.Container, info.Container, StringComparison.Ordinal))
            throw new ArgumentException("Credential and profile target differ.");
    }
}

public static partial class Redaction
{
    public static string Clean(string? value)
    {
        if (value is null) return "";
        var safe = SecretField().Replace(value, "$1=[redacted]");
        safe = SasUrl().Replace(safe, "[storage URL redacted]");
        safe = safe.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "[user]", StringComparison.OrdinalIgnoreCase);
        return safe.Length > 2000 ? safe[..2000] : safe;
    }
    [GeneratedRegex("https?://[^\\s\\\"<>]*\\?[^\\s\\\"<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex SasUrl();
    [GeneratedRegex("""\b(sig|sas_url|account_key|access_token|refresh_token|client_secret|client_assertion|password|authorization|token|secret)\b["']?\s*[=:]\s*(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?:Bearer|Basic)\s+[^\s,;]+|[^\s,;]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();
}