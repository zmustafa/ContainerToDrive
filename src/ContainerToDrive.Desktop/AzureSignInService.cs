using System.IO;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Desktop;

internal sealed record AzureSubscriptionChoice(string Id, string Name, string TenantId)
{
    public override string ToString() => Name;
}

internal sealed record AzureResourceGroupChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed record AzureStorageAccountChoice(string Id, string Name)
{
    public string ResourceGroupName => new ResourceIdentifier(Id).ResourceGroupName ?? "";
    public override string ToString() => Name;
}

internal sealed class AzureSignInService : IDisposable
{
    private const int MaxDiscoveryItems = 2000;
    private const string RecordName = "entra-authentication-record";
    private readonly ProtectedSettingsStore _settings;
    private readonly TokenCachePersistenceOptions _cacheOptions;
    private readonly string? _configuredClientId;
    private readonly Dictionary<string, ArmClient> _tenantArms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _subscriptionTenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _accountTenants = new(StringComparer.OrdinalIgnoreCase);
    private InteractiveBrowserCredential? _credential;
    private AuthenticationRecord? _record;
    private ArmClient? _arm;

    internal AzureSignInService(string dataRoot)
    {
        _settings = new ProtectedSettingsStore(dataRoot);
        _configuredClientId = ReadConfiguredClientId();
        var cacheIdentity = AppPaths.CurrentSid + "\n" + Path.GetFullPath(dataRoot).ToUpperInvariant();
        _cacheOptions = new TokenCachePersistenceOptions
        {
            Name = "ContainerToDrive-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)))[..24]
        };
        Restore();
    }

    internal bool IsSignedIn => _credential is not null && _arm is not null && _record is not null;
    internal string AccountLabel { get; private set; } = "Not signed in";
    internal string TenantId => _record?.TenantId ?? "";
    internal bool UsesProjectClientId => _configuredClientId is not null;

    internal async Task SignInAsync(CancellationToken token, string? tenantId = null)
    {
        ClearSession(deleteRecord: true);
        try
        {
            var (credential, record) = await AuthenticateInteractivelyAsync(
                tenantId ?? "organizations", record: null, token);
            _record = record;
            _credential = CreateCredential(record.TenantId, record, automaticAuthentication: false);
            _arm = new ArmClient(_credential);
            _tenantArms[record.TenantId] = _arm;
            AccountLabel = string.IsNullOrWhiteSpace(record.Username) ? "Signed in Azure account" : record.Username;
            Persist(record);
        }
        catch
        {
            ClearSession(deleteRecord: true);
            throw;
        }
    }

    internal async Task EnsureArmAuthorizationAsync(bool allowInteractive, CancellationToken token)
    {
        var credential = _credential ?? throw new InvalidOperationException("Sign in to Azure first.");
        try
        {
            _ = await credential.GetTokenAsync(
                ArmTokenRequest(), token);
        }
        catch (Exception exception) when (allowInteractive &&
            exception is AuthenticationRequiredException or AuthenticationFailedException)
        {
            var record = _record ?? throw new InvalidOperationException("Sign in to Azure first.");
            var (interactive, refreshed) = await AuthenticateInteractivelyAsync(
                record.TenantId, record, token);
            _record = refreshed;
            _credential = interactive;
            _arm = new ArmClient(interactive);
            _tenantArms.Clear();
            _tenantArms[refreshed.TenantId] = _arm;
            _subscriptionTenants.Clear();
            _accountTenants.Clear();
            AccountLabel = string.IsNullOrWhiteSpace(refreshed.Username) ? "Signed in Azure account" : refreshed.Username;
            Persist(refreshed);
        }
    }

    internal async Task<IReadOnlyList<AzureSubscriptionChoice>> GetSubscriptionsAsync(CancellationToken token)
    {
        var primary = RequireArm();
        var tenantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TenantId };
        try
        {
            await foreach (var tenant in primary.GetTenants().GetAllAsync(token))
                if (tenant.Data.TenantId is { } tenantId && tenantId != Guid.Empty)
                    tenantIds.Add(tenantId.ToString());
        }
        catch (Azure.RequestFailedException)
        {
            // Home-tenant subscription discovery below remains useful when tenant listing is restricted.
        }

        var results = new Dictionary<string, AzureSubscriptionChoice>(StringComparer.OrdinalIgnoreCase);
        Exception? lastFailure = null;
        foreach (var tenantId in tenantIds)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var arm = ArmForTenant(tenantId);
                await foreach (var subscription in arm.GetSubscriptions().GetAllAsync(token))
                {
                    if (results.Count == MaxDiscoveryItems) throw TooMany("subscriptions");
                    var id = subscription.Data.SubscriptionId;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    _subscriptionTenants[id] = tenantId;
                    results[id] = new(id, subscription.Data.DisplayName ?? id, tenantId);
                }
            }
            catch (Exception exception) when (exception is Azure.RequestFailedException or AuthenticationFailedException or AuthenticationRequiredException)
            {
                lastFailure = exception;
            }
        }
        if (results.Count == 0 && lastFailure is not null)
            throw new InvalidOperationException("Azure subscriptions could not be refreshed for the signed-in account.");
        return results.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal async Task<IReadOnlyList<AzureResourceGroupChoice>> GetResourceGroupsAsync(string subscriptionId, CancellationToken token)
    {
        if (!Guid.TryParse(subscriptionId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("The selected subscription is invalid.");
        if (!_subscriptionTenants.TryGetValue(subscriptionId, out var tenantId)) tenantId = TenantId;
        var subscription = ArmForTenant(tenantId).GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var results = new List<AzureResourceGroupChoice>();
        await foreach (var group in subscription.GetResourceGroups().GetAllAsync(cancellationToken: token))
        {
            if (results.Count == MaxDiscoveryItems) throw TooMany("resource groups");
            results.Add(new(group.Id.ToString(), group.Data.Name));
        }
        return results.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal Task<IReadOnlyList<AzureStorageAccountChoice>> GetStorageAccountsAsync(string resourceGroupId, CancellationToken token) =>
        GetStorageAccountsAsync(ExtractSubscriptionId(resourceGroupId), resourceGroupId, token);

    internal async Task<IReadOnlyList<AzureStorageAccountChoice>> GetStorageAccountsAsync(string subscriptionId, string? resourceGroupId, CancellationToken token)
    {
        var scope = StorageAccountScope(subscriptionId, resourceGroupId);
        if (!_subscriptionTenants.TryGetValue(subscriptionId, out var tenantId)) tenantId = TenantId;
        var arm = ArmForTenant(tenantId);
        var resources = resourceGroupId is null
            ? arm.GetSubscriptionResource(scope).GetGenericResourcesAsync(filter: "resourceType eq 'Microsoft.Storage/storageAccounts'", cancellationToken: token)
            : arm.GetResourceGroupResource(scope).GetGenericResourcesAsync(filter: "resourceType eq 'Microsoft.Storage/storageAccounts'", cancellationToken: token);
        var results = new List<AzureStorageAccountChoice>();
        await foreach (var resource in resources)
        {
            if (results.Count == MaxDiscoveryItems) throw TooMany("storage accounts");
            var accountName = Validation.ValidateAccountName(resource.Data.Name);
            _accountTenants[accountName] = tenantId;
            results.Add(new(resource.Id.ToString(), accountName));
        }
        return results.OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
    }

    internal static ResourceIdentifier StorageAccountScope(string subscriptionId, string? resourceGroupId)
    {
        if (!Guid.TryParse(subscriptionId, out var subscription) || subscription == Guid.Empty)
            throw new ArgumentException("Select a valid subscription.");
        if (resourceGroupId is null) return new ResourceIdentifier($"/subscriptions/{subscriptionId}");
        var group = new ResourceIdentifier(resourceGroupId);
        if (!string.Equals(group.ResourceType.ToString(), "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(group.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The resource group must belong to the selected subscription.");
        return group;
    }

    internal static bool IsAccountInScope(string subscriptionId, string? resourceGroupId, AzureStorageAccountChoice account)
    {
        try
        {
            var scope = StorageAccountScope(subscriptionId, resourceGroupId);
            var identifier = new ResourceIdentifier(account.Id);
            return string.Equals(identifier.ResourceType.ToString(), "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(identifier.SubscriptionId, scope.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(identifier.ResourceGroupName) &&
                string.Equals(identifier.Name, account.Name, StringComparison.OrdinalIgnoreCase) &&
                (resourceGroupId is null || string.Equals(identifier.ResourceGroupName, scope.ResourceGroupName, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
    }

    internal async Task<IReadOnlyList<string>> GetContainersAsync(string accountName, CancellationToken token)
    {
        var service = CreateBlobService(accountName);
        var results = new List<string>();
        await foreach (var container in service.GetBlobContainersAsync(cancellationToken: token))
        {
            if (results.Count == MaxDiscoveryItems) throw TooMany("blob containers");
            results.Add(container.Name);
        }
        return results;
    }

    internal async Task<string> CreateDelegationSasAsync(string accountName, string containerName, bool readOnly, CancellationToken token)
    {
        accountName = Validation.ValidateAccountName(accountName);
        containerName = Validation.ValidateContainerName(containerName);
        var endpoint = Validation.EndpointForAccount(accountName);
        var service = CreateBlobService(accountName);
        var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expires = DateTimeOffset.UtcNow.AddDays(1);
        var delegationKey = await service.GetUserDelegationKeyAsync(starts, expires, token);
        var builder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            Resource = "c",
            StartsOn = starts,
            ExpiresOn = expires,
            Protocol = SasProtocol.Https
        };
        var permissions = BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List;
        if (!readOnly)
            permissions |= BlobContainerSasPermissions.Add | BlobContainerSasPermissions.Create |
                BlobContainerSasPermissions.Write | BlobContainerSasPermissions.Delete;
        builder.SetPermissions(permissions);
        var uri = new BlobUriBuilder(new Uri($"{endpoint}/{containerName}"))
        {
            Sas = builder.ToSasQueryParameters(delegationKey.Value, accountName)
        };
        return uri.ToUri().AbsoluteUri;
    }

    internal void SignOut() => ClearSession(deleteRecord: true);

    public void Dispose() => ClearSession(deleteRecord: false);

    private void Restore()
    {
        byte[]? bytes = null;
        try
        {
            bytes = _settings.Read(RecordName);
            if (bytes is null) return;
            using var stream = new MemoryStream(bytes, writable: false);
            var record = AuthenticationRecord.Deserialize(stream);
            if (_configuredClientId is not null && !string.Equals(_configuredClientId, record.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Delete(RecordName);
                return;
            }
            _record = record;
            _credential = CreateCredential(record.TenantId, record, automaticAuthentication: false);
            _arm = new ArmClient(_credential);
            _tenantArms[record.TenantId] = _arm;
            AccountLabel = string.IsNullOrWhiteSpace(record.Username) ? "Saved Azure account" : record.Username;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or CryptographicException or AuthenticationFailedException)
        {
            _settings.Delete(RecordName);
            ClearSession(deleteRecord: false);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void Persist(AuthenticationRecord record)
    {
        using var stream = new MemoryStream();
        record.Serialize(stream);
        _settings.Write(RecordName, stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private InteractiveBrowserCredential CreateCredential(string tenantId, AuthenticationRecord? record, bool automaticAuthentication)
    {
        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = tenantId == "organizations" ? tenantId : ValidateTenant(tenantId),
            RedirectUri = new Uri("http://localhost"),
            TokenCachePersistenceOptions = _cacheOptions,
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = !automaticAuthentication
        };
        options.AdditionallyAllowedTenants.Add("*");
        var clientId = _configuredClientId ?? record?.ClientId;
        if (clientId is not null) options.ClientId = clientId;
        return new InteractiveBrowserCredential(options);
    }

    private Task<(InteractiveBrowserCredential Credential, AuthenticationRecord Record)> AuthenticateInteractivelyAsync(
        string tenantId, AuthenticationRecord? record, CancellationToken token) =>
        InteractiveAuthentication.RunAsync(async () =>
        {
            var credential = CreateCredential(tenantId, record, automaticAuthentication: true);
            var authenticated = await credential.AuthenticateAsync(ArmTokenRequest(), token).ConfigureAwait(false);
            return (credential, authenticated);
        }, token);

    private ArmClient ArmForTenant(string tenantId)
    {
        if (_tenantArms.TryGetValue(tenantId, out var existing)) return existing;
        var record = _record ?? throw new InvalidOperationException("Sign in to Azure first.");
        var arm = new ArmClient(CreateCredential(tenantId, record, automaticAuthentication: false));
        _tenantArms[tenantId] = arm;
        return arm;
    }

    private ArmClient RequireArm() => _arm ?? throw new InvalidOperationException("Sign in to Azure first.");

    private BlobServiceClient CreateBlobService(string accountName)
    {
        accountName = Validation.ValidateAccountName(accountName);
        var record = _record ?? throw new InvalidOperationException("Sign in to Azure first.");
        var tenantId = _accountTenants.GetValueOrDefault(accountName, record.TenantId);
        return new BlobServiceClient(new Uri(Validation.EndpointForAccount(accountName)),
            CreateCredential(tenantId, record, automaticAuthentication: false));
    }

    private void ClearSession(bool deleteRecord)
    {
        if (deleteRecord) _settings.Delete(RecordName);
        _credential = null;
        _record = null;
        _arm = null;
        _tenantArms.Clear();
        _subscriptionTenants.Clear();
        _accountTenants.Clear();
        AccountLabel = "Not signed in";
    }

    private static string? ReadConfiguredClientId()
    {
        var value = Environment.GetEnvironmentVariable("CONTAINERTODRIVE_AZURE_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetEnvironmentVariable("BLOBTODRIVE_AZURE_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? value
            : throw new InvalidOperationException("The configured Azure public-client ID is invalid.");
    }

    private static TokenRequestContext ArmTokenRequest() => new(
        [ArmEnvironment.AzurePublicCloud.DefaultScope],
        parentRequestId: null,
        claims: null,
        tenantId: null,
        isCaeEnabled: true);

    private static string ExtractSubscriptionId(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < parts.Length; index++)
            if (string.Equals(parts[index], "subscriptions", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(parts[index + 1], out var parsed) && parsed != Guid.Empty)
                return parts[index + 1];
        throw new ArgumentException("The resource group does not belong to a valid subscription.");
    }

    private static InvalidOperationException TooMany(string resource) =>
        new($"More than {MaxDiscoveryItems} {resource} were returned. Narrow access or use a more specific account.");

    private static string ValidateTenant(string tenantId) =>
        Guid.TryParse(tenantId, out var parsed) && parsed != Guid.Empty
            ? tenantId
            : throw new ArgumentException("The saved Azure tenant is invalid.");
}
