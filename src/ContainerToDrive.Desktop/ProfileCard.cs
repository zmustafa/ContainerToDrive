using System.ComponentModel;
using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

public sealed class ProfileCard : INotifyPropertyChanged
{
    private bool _online;
    private bool _canMount;
    private bool _busy;
    private TransferSummary? _transfers;
    private EndpointResolution? _endpoint;
    public Profile Profile { get; private set; } = new();
    public MountStatus? Status { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => DisplayText.Clean(Profile.Name);
    public string Source => DisplayText.Resource(Profile);
    public bool EndpointVisible { get; private set; } = true;
    public bool EndpointFresh => _online && _endpoint?.ResolvedAt is { } observed &&
        DateTimeOffset.UtcNow - observed < TimeSpan.FromSeconds(75) && observed <= DateTimeOffset.UtcNow.AddSeconds(5);
    private string EndpointAddresses => _endpoint is null || _endpoint.Addresses.Count == 0 ? "Unavailable" :
        string.Join(", ", _endpoint.Addresses.Take(2)) + (_endpoint.Addresses.Count > 2 ? $" (+{_endpoint.Addresses.Count - 2})" : "");
    public string EndpointInfo => _endpoint is null ? "Host IP not resolved"
        : _endpoint.ResolvedAt is null ? "Host IP unavailable | Endpoint type unknown"
        : !EndpointFresh ? "Endpoint result stale | Last IP: " + EndpointAddresses
        : (_endpoint.Kind switch
        {
            EndpointKind.Private => "Private endpoint (DNS)",
            EndpointKind.Public => "Public endpoint (DNS)",
            EndpointKind.Mixed => "Mixed endpoints (DNS)",
            _ => "Endpoint type unknown (DNS)"
        }) + " | " + EndpointAddresses;
    public string EndpointDetails => _endpoint is null ? "DNS host IP: Not observed"
        : $"DNS host: {_endpoint.Host}\nResolved IPs: {(_endpoint.Addresses.Count == 0 ? "Unavailable" : string.Join(", ", _endpoint.Addresses))}\nDNS observed: {DisplayText.Time(_endpoint.ResolvedAt)}" + (EndpointFresh ? "" : " (unavailable or stale)");
    public string EndpointHelp => EndpointDetails + "\nPrivate/public is inferred from DNS IP ranges, not verification of an Azure Private Link resource or the mount's active TCP connection. Proxies and existing connections may use a different route.";
    public string Authentication => "Authentication: " + DisplayText.Authentication(Profile);
    public string Drive => Profile.DriveLetter.Length == 1 && Profile.DriveLetter[0] is >= 'D' and <= 'Z' ? Profile.DriveLetter + ":" : "?";
    public string Mode => Profile.ReadOnly ? "READ-ONLY" : "WRITABLE";
    public string SettingsSummary => $"{Drive} | {Mode} | Auto-mount {(Profile.AutoMount ? "on" : "off")}";
    public string Expiry => Profile.AuthenticationKind == AuthenticationKind.AccountKey
        ? "Account keys do not carry an expiry; Azure policy or key rotation can still revoke access."
        : Profile.ExpiresAt is { } expiry
        ? (expiry <= DateTimeOffset.UtcNow ? "Access expired · " : "Access expires · ") + DisplayText.Time(expiry)
        : "Access expiry unknown · stored policy may apply";
    public string RenewLabel => Profile.AuthenticationKind == AuthenticationKind.MicrosoftEntra ? "_Sign in to renew" : "_Renew credential";
    public bool Fresh => _online && Status?.ObservedAt is { } observed &&
        DateTimeOffset.UtcNow - observed < TimeSpan.FromSeconds(20) && observed <= DateTimeOffset.UtcNow.AddSeconds(5);
    public string State => !_online ? "Status unavailable" : Status is null ? "State not reported" : Status.RecoveryRequired ? "Recovery required" : Status.Phase switch
    {
        MountPhase.Unmounted => "Not mounted",
        MountPhase.Starting => "Connecting…",
        MountPhase.Mounted => Fresh ? (Profile.ReadOnly ? "Connected · read-only" : "Connected · writable") : "Mounted · observation stale",
        MountPhase.DisconnectRequested => "Disconnect requested",
        MountPhase.Faulted => "Attention required",
        _ => "State not reported"
    };
    public string Observation => "Last worker observation: " + DisplayText.Time(Status?.ObservedAt) + (Fresh ? "" : " · unavailable or stale");
    public string Cache => "Local cache: " + DisplayText.Bytes(Status?.CacheBytes) + $" / {DisplayText.Bytes(Profile.CacheMaxBytes)} target";
    public string TransferTotal => _transfers?.StartedAt is null ? "No transfer history recorded" : "Recorded transfer total: " + TransferDashboard.FormatBytes(_transfers.Bytes);
    public string TransferDetails => _transfers?.Unavailable == true ? "Transfer statistics unavailable; recorded totals may be incomplete."
        : _transfers?.StartedAt is null ? "--" : $"{_transfers.CompletedTransfers:N0} completed transfers | {_transfers.Errors:N0} engine errors | Last sample {DisplayText.Time(_transfers.ObservedAt)}";
    public string Uploads => !Fresh ? "Upload status unavailable; previous observations are not current."
        : Profile.ReadOnly ? "Read-only mount · writes through this drive are disabled."
        : Status!.Uploads switch
        {
            UploadState.NoQueuedUploadsReported => "No queued uploads reported. Open files may still contain unsent changes.",
            UploadState.Pending => "Uploads pending · keep this computer and controller running.",
            UploadState.Uploading => "Uploading · changes may not yet be in Azure.",
            UploadState.Failed => "Upload failures reported · preserve the local cache and review recovery.",
            _ => "Upload status unknown · do not assume changes reached Azure."
        };
    public string Counts => $"Worker reports: {Count(Status?.Queued)} queued · {Count(Status?.Uploading)} uploading" + (Fresh ? "" : " (last known)");
    public string Recovery => "An interrupted or uncertain session needs review. Resuming this same connection may upload cached changes to Azure. Do not delete or move its cache.";
    public bool NeedsRecovery => Status?.RecoveryRequired == true;
    public string MountLabel => NeedsRecovery ? "_Resume…" : "_Mount";
    public bool CanMount => _online && !_busy && _canMount && Status is not null &&
        Status.Phase is MountPhase.Unmounted or MountPhase.Faulted;
    public bool CanOpen => _online && !_busy && Fresh && Status?.Phase == MountPhase.Mounted;
    public bool CanDisconnect => _online && !_busy && Status is not null &&
        Status.Phase is MountPhase.Starting or MountPhase.Mounted or MountPhase.DisconnectRequested;
    public bool CanForce => _online && !_busy && Status is not null && Status.Phase != MountPhase.Unmounted;
    public bool CanEdit => _online && !_busy && Status?.Phase == MountPhase.Unmounted && !NeedsRecovery;
    public bool CanRemove => CanEdit;
    public bool CanRenew => _online && !_busy;
    public bool CanValidate => _online && !_busy;
    public bool CanClone => _online && !_busy;

    internal void Update(Profile profile, MountStatus? status, bool online, bool canMount, bool busy, TransferSummary? transfers = null, bool endpointVisible = true)
    {
        if (!string.Equals(Profile.Endpoint.TrimEnd('/'), profile.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) _endpoint = null;
        Profile = profile;
        Status = status;
        _online = online;
        _canMount = canMount;
        _busy = busy;
        _transfers = transfers;
        EndpointVisible = endpointVisible;
        if (!endpointVisible) _endpoint = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal void SetEndpoint(EndpointResolution result)
    {
        if (!Uri.TryCreate(Profile.Endpoint, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.DnsSafeHost, result.Host, StringComparison.OrdinalIgnoreCase)) return;
        _endpoint = result;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string Count(int? value) => value is >= 0 ? value.Value.ToString(System.Globalization.CultureInfo.CurrentCulture) : "?";
}