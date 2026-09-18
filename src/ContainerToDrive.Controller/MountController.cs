using System.Text.Json;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ContainerToDrive.Core;
using ContainerToDrive.Rclone;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Controller;

public sealed class MountController : IDisposable
{
    private readonly ProfileStore _store;
    private readonly TransferHistoryStore _transfers;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _statisticsCursor;
    private readonly Dictionary<Guid, RcloneWorker> _workers = [];
    private readonly Dictionary<Guid, MountStatus> _status = [];
    private bool _engineAvailable;
    public bool ShutdownRequested { get; private set; }

    public MountController(string root)
    {
        _store = new(root);
        _transfers = new(root);
        try { EngineLocator.FindVerified(); _engineAvailable = true; } catch (Exception) { _engineAvailable = false; }
    }

    public async Task AutoMountAsync()
    {
        if (AppPaths.IsElevated || !AppPaths.WinFspInstalled || !_engineAvailable) return;
        foreach (var profile in _store.List().Where(p => p.AutoMount && _store.ReadIntent(p.Id)?.Uncertain != true))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await HandleAsync(new Request { Operation = "Mount", ProfileId = profile.Id }, timeout.Token);
        }
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try { return await HandleCoreAsync(request, token); }
        finally { _operations.Release(); }
    }

    public async Task RunStatisticsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await _operations.WaitAsync(token);
                try
                {
                    using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cycle.CancelAfter(TimeSpan.FromSeconds(8));
                    var workers = _workers.ToArray();
                    for (var sampled = 0; sampled < workers.Length; sampled++)
                    {
                        if (cycle.IsCancellationRequested) break;
                        _statisticsCursor %= workers.Length;
                        var entry = workers[_statisticsCursor++];
                        await CaptureStatisticsAsync(entry.Key, entry.Value, cycle.Token);
                    }
                }
                finally { _operations.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CaptureStatisticsAsync(Guid profileId, RcloneWorker worker, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var counters = await worker.ReadTransferCountersAsync(timeout.Token);
            _transfers.Record(profileId, worker.SessionId, counters, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { _transfers.MarkUnavailable(profileId); }
    }

    private async Task<Response> HandleCoreAsync(Request request, CancellationToken token)
    {
        try
        {
            if (request.ProtocolVersion != Wire.Version) return Response.Fail("Unsupported controller protocol.");
            switch (request.Operation)
            {
                case "Status": return Response.Ok(snapshot: await SnapshotAsync(token));
                case "RelocateData":
                    if (!request.Confirm || _workers.Count != 0 || !DataRelocation.CanRelocate(await SnapshotAsync(token)))
                        return Response.Fail("Disconnect all drives and resolve recovery before confirming a data location change.");
                    var destination = await DataRelocation.RelocateAsync(_store, request.DestinationDataRoot ?? throw new ArgumentException("A destination folder is required."), token);
                    ShutdownRequested = true;
                    return new() { Success = true, RelocatedDataRoot = destination, Message = "Data copied and verified. Reopen the app at the new location. The original is retained." };
                case "Statistics":
                    var ids = request.ProfileId == Guid.Empty ? _store.List().Select(profile => profile.Id) : [Required(request.ProfileId).Id];
                    return new() { Success = true, Statistics = _transfers.Report(ids, request.StatisticsDays, DateTimeOffset.UtcNow) };
                case "TestCapabilities": return await TestCapabilitiesAsync(token);
                case "Clone":
                    CloneProfile(Required(request.ProfileId));
                    return Response.Ok("Connection cloned without mounting. Automatic mounting is disabled.", await SnapshotAsync(token));
                case "Save":
                {
                    var profile = request.Profile ?? throw new ArgumentException("Profile is required.");
                    if (_workers.ContainsKey(profile.Id) || _store.ReadIntent(profile.Id)?.Uncertain == true)
                        throw new InvalidOperationException("Disconnect and resolve recovery before editing this profile.");
                    var old = _store.Find(profile.Id);
                    if (old is not null && Validation.Identity(old) != Validation.Identity(profile)) throw new InvalidOperationException("Create a new profile for a different storage source.");
                    Validation.ValidateCredential(profile, request.Credential ?? _store.ReadCredential(profile.Id));
                    _store.Save(profile, request.Credential);
                    return Response.Ok("Profile saved securely.", await SnapshotAsync(token));
                }
                case "Validate":
                {
                    var profile = request.Profile ?? Required(request.ProfileId);
                    var credential = request.Credential ?? _store.ReadCredential(profile.Id);
                    Validation.ValidateCredential(profile, credential);
                    var transient = profile with { Id = Guid.NewGuid() };
                    await using var worker = new RcloneWorker(transient, _store.RuntimePath(transient.Id), _store.CachePath(transient.Id));
                    await worker.StartAsync(credential, token); await worker.ValidateAsync(token);
                    return Response.Ok("Container listing succeeded. This does not prove write access.");
                }
                case "DiscoverContainers": return await DiscoverContainersAsync(request, token);
                case "Mount": return await MountAsync(Required(request.ProfileId), request.Confirm, token);
                case "Disconnect": return await DisconnectAsync(Required(request.ProfileId), request.Force && request.Confirm, token);
                case "Refresh":
                    if (!_workers.TryGetValue(request.ProfileId, out var refresh)) throw new InvalidOperationException("The profile is not mounted.");
                    await refresh.RefreshAsync(token); return Response.Ok("Directory refresh requested.", await SnapshotAsync(token));
                case "Renew":
                    if (_workers.ContainsKey(request.ProfileId)) throw new InvalidOperationException("Disconnect before renewing the credential. Cached data will be preserved.");
                    _store.Renew(request.ProfileId, request.Credential ?? throw new ArgumentException("A replacement credential is required."));
                    return Response.Ok("Credential renewed; cache identity retained.", await SnapshotAsync(token));
                case "Remove":
                    if (_workers.ContainsKey(request.ProfileId) || _store.ReadIntent(request.ProfileId)?.Uncertain == true) throw new InvalidOperationException("Resolve mount/recovery state before removing the profile.");
                    _store.Remove(request.ProfileId); _status.Remove(request.ProfileId);
                    return Response.Ok("Profile removed. Cached data was not deleted.", await SnapshotAsync(token));
                case "Export": return await ExportAsync(token);
                case "Shutdown":
                    if (_workers.Count != 0) throw new InvalidOperationException("Disconnect all mounts before exiting.");
                    ShutdownRequested = true; return Response.Ok("Controller stopped.");
                default: return Response.Fail("Unsupported controller operation.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { return Response.Fail(Redaction.Clean(ex.Message)); }
    }

    private Profile Required(Guid id) => _store.Find(id) ?? throw new KeyNotFoundException("Profile not found.");

    private void CloneProfile(Profile source)
    {
        var profiles = _store.List();
        var reserved = profiles.Select(profile => profile.DriveLetter)
            .Concat(Environment.GetLogicalDrives().Select(drive => drive[..1]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var driveLetter = Enumerable.Range('D', 23).Select(code => ((char)code).ToString())
            .FirstOrDefault(letter => !reserved.Contains(letter))
            ?? throw new InvalidOperationException("No available drive letters. Free a drive letter or remove an unused saved connection before cloning.");
        var names = profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suffix = " (copy)";
        var name = source.Name[..Math.Min(source.Name.Length, 100 - suffix.Length)] + suffix;
        for (var number = 2; names.Contains(name); number++)
        {
            suffix = $" (copy {number})";
            name = source.Name[..Math.Min(source.Name.Length, 100 - suffix.Length)] + suffix;
        }
        var copy = source with
        {
            Id = Guid.NewGuid(), Name = name, DriveLetter = driveLetter,
            Revision = 0, CredentialRevision = 0, AutoMount = false
        };
        var credential = _store.ReadCredential(source.Id) with { ExpectedRevision = 0 };
        _store.Save(copy, credential);
    }

    private static async Task<Response> DiscoverContainersAsync(Request request, CancellationToken token)
    {
        var accountName = Validation.ValidateAccountName(request.StorageAccountName);
        var credential = request.Credential ?? throw new ArgumentException("An account-key credential is required.");
        if (credential.Kind != AuthenticationKind.AccountKey || credential.SasUrl is not null || credential.AccountKey is null)
            throw new ArgumentException("Account-key container discovery requires exactly one account key.");
        Validation.ValidateAccountKey(credential.AccountKey);
        if (request.PageSize is < 1 or > 100 || request.ContinuationToken is { Length: > 8192 } ||
            request.ContinuationToken?.Any(char.IsControl) == true)
            throw new ArgumentException("The container page request is invalid.");

        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(1);
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        var service = new BlobServiceClient(
            new Uri(Validation.EndpointForAccount(accountName)),
            new StorageSharedKeyCredential(accountName, credential.AccountKey),
            options);
        try
        {
            await foreach (Azure.Page<BlobContainerItem> page in service.GetBlobContainersAsync(cancellationToken: token)
                .AsPages(request.ContinuationToken, request.PageSize).WithCancellation(token))
            {
                return new Response
                {
                    Success = true,
                    Message = "Container page loaded.",
                    Containers = page.Values.Select(item => item.Name).ToList(),
                    ContinuationToken = page.ContinuationToken
                };
            }
            return new Response { Success = true, Message = "No containers found.", Containers = [] };
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 403)
        {
            throw new UnauthorizedAccessException("Azure denied account-key discovery. The key may be invalid or Shared Key access may be disabled.");
        }
        catch (Azure.RequestFailedException)
        {
            throw new IOException("Azure container discovery failed. Check the account name, network, firewall, and key.");
        }
    }
    private async Task<Response> MountAsync(Profile profile, bool confirmedRecovery, CancellationToken token)
    {
        if (AppPaths.IsElevated) throw new InvalidOperationException("Start ContainerToDrive normally, not as administrator, so Explorer can see the drive.");
        if (!AppPaths.WinFspInstalled) throw new InvalidOperationException("WinFsp is not installed. Install the verified official prerequisite, then reopen ContainerToDrive.");
        if (_workers.ContainsKey(profile.Id)) throw new InvalidOperationException("This profile already owns a worker. Disconnect it before retrying.");
        Validation.ValidateProfile(profile);
        var intent = _store.ReadIntent(profile.Id);
        if (intent?.Uncertain == true && !confirmedRecovery) throw new InvalidOperationException("Previous mount was interrupted. Confirm recovery before reusing its cache.");
        if (intent is not null && (intent.Identity != Validation.Identity(profile) || intent.EngineVersion != EngineLocator.Version)) throw new InvalidOperationException("Recovery identity/version differs. Preserve the cache and request support.");
        foreach (var id in _workers.Keys)
        {
            var other = Required(id);
            if (other.DriveLetter == profile.DriveLetter || (Validation.Overlaps(profile, other) && (!profile.ReadOnly || !other.ReadOnly)))
                throw new InvalidOperationException("Drive letter or writable source overlaps an active mount.");
        }
        var credential = _store.ReadCredential(profile.Id);
        Validation.ValidateCredential(profile, credential);
        _store.WriteIntent(new() { ProfileId = profile.Id, Identity = Validation.Identity(profile), EngineVersion = EngineLocator.Version, WasWritable = !profile.ReadOnly, Uncertain = !profile.ReadOnly });
        var worker = new RcloneWorker(profile, _store.RuntimePath(profile.Id), _store.CachePath(profile.Id));
        try
        {
            await worker.StartAsync(credential, token);
            await worker.ValidateAsync(token);
            await worker.MountAsync(token);
            _workers.Add(profile.Id, worker);
            await CaptureStatisticsAsync(profile.Id, worker, token);
            _status[profile.Id] = await worker.ObserveAsync(token);
            return Response.Ok($"Mounted {profile.DriveLetter}: {(profile.ReadOnly ? "read-only" : "writable")}.", await SnapshotAsync(token));
        }
        catch
        {
            await worker.DisposeAsync();
            _status[profile.Id] = new() { ProfileId = profile.Id, Phase = MountPhase.Faulted, Message = "Mount failed; cache retained.", RecoveryRequired = true };
            throw;
        }
    }

    private async Task<Response> DisconnectAsync(Profile profile, bool force, CancellationToken token)
    {
        if (!_workers.TryGetValue(profile.Id, out var worker)) return Response.Fail("No active worker. Any preserved recovery state remains unchanged.");
        var state = await worker.ObserveAsync(token);
        if (!profile.ReadOnly && !force && state.Uploads != UploadState.NoQueuedUploadsReported)
            return Response.Fail("Writable disconnect requires a current observation with no queued, active, or failed uploads. Close applications and retry after uploads finish.");
        if (!force) await worker.UnmountAsync(token);
        else { try { await worker.UnmountAsync(token); } catch (Exception) { } }
        await CaptureStatisticsAsync(profile.Id, worker, token);
        await worker.DisposeAsync(); _workers.Remove(profile.Id);
        var uncertain = force && (!profile.ReadOnly || state.Phase != MountPhase.Mounted);
        _store.WriteIntent(new() { ProfileId = profile.Id, Identity = Validation.Identity(profile), EngineVersion = EngineLocator.Version, WasWritable = !profile.ReadOnly, Uncertain = uncertain });
        _status[profile.Id] = new() { ProfileId = profile.Id, Phase = MountPhase.Unmounted, Uploads = profile.ReadOnly ? UploadState.NotApplicable : UploadState.Unknown, Message = uncertain ? "Disconnected; recovery inspection required." : "Disconnected - cache retained.", RecoveryRequired = uncertain };
        return Response.Ok("Drive disconnected. Cache retained.", await SnapshotAsync(token));
    }

    public async Task<AppSnapshot> SnapshotAsync(CancellationToken token)
    {
        var profiles = _store.List(); var statuses = new List<MountStatus>();
        foreach (var profile in profiles)
        {
            if (_workers.TryGetValue(profile.Id, out var worker)) _status[profile.Id] = await worker.ObserveAsync(token);
            if (_status.TryGetValue(profile.Id, out var status)) statuses.Add(status);
            else
            {
                var recovery = _store.ReadIntent(profile.Id)?.Uncertain == true;
                statuses.Add(new() { ProfileId = profile.Id, Phase = MountPhase.Unmounted, Uploads = UploadState.Unknown, RecoveryRequired = recovery, Message = recovery ? "Previous stop was uncertain; cache retained." : "Not mounted" });
            }
        }
        return new() { Profiles = profiles, Mounts = statuses, Transfers = profiles.Select(profile => _transfers.Summary(profile.Id, _workers.TryGetValue(profile.Id, out var worker) && worker.Alive)).ToList(), WinFspInstalled = AppPaths.WinFspInstalled, EngineAvailable = _engineAvailable, EngineWorkerCount = _workers.Values.Count(worker => worker.Alive), WritableEnabled = true, Elevated = AppPaths.IsElevated, DataRoot = _store.DataRoot };
    }

    private async Task<Response> TestCapabilitiesAsync(CancellationToken token)
    {
        var checks = new List<CapabilityCheck> { new("Backend", true, "Authenticated controller connection succeeded.") };
        _engineAvailable = false;
        try
        {
            checks.AddRange(await EngineLocator.CheckCapabilitiesAsync(token));
            _engineAvailable = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            checks.Add(new("Engine", false, "Engine verification or startup failed. Install or repair dependencies and test again."));
        }
        var driverInstalled = AppPaths.WinFspInstalled;
        checks.Add(new("WinFsp", driverInstalled, driverInstalled ? "Installation detected. Driver loading requires an actual mount." : "WinFsp is not installed or its library is unavailable."));
        var normalSession = !AppPaths.IsElevated;
        checks.Add(new("Windows session", normalSession, normalSession ? "Non-administrator session; Explorer mounting is allowed." : "Restart the app normally to mount drives in Explorer."));
        try
        {
            var occupied = DriveInfo.GetDrives().Select(drive => drive.Name[0]).ToHashSet();
            var available = Enumerable.Range('D', 'Z' - 'D' + 1).Count(letter => !occupied.Contains((char)letter));
            checks.Add(new("Drive letters", available > 0, $"{available} drive letters currently available."));
        }
        catch (IOException) { checks.Add(new("Drive letters", false, "Drive availability could not be checked.")); }
        try
        {
            var path = Path.Combine(_store.DataRoot, "capability-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await probe.WriteAsync(new byte[] { 1 }, token);
            await probe.FlushAsync(token);
            checks.Add(new("Local storage", true, "Protected application storage is writable."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { checks.Add(new("Local storage", false, "Protected application storage could not be written.")); }
        return new() { Success = true, Capabilities = checks, Snapshot = await SnapshotAsync(token), Message = "Local capability checks completed. Azure access and writes were not tested." };
    }

    private async Task<Response> ExportAsync(CancellationToken token)
    {
        var snapshot = await SnapshotAsync(token);
        var directory = Path.Combine(_store.DataRoot, "diagnostics"); AppPaths.SecureDirectory(directory);
        var path = Path.Combine(directory, "diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        // Deliberately omit profiles, endpoints, user names, contents, queue names and all secrets.
        var report = new { schemaVersion = 1, generatedAt = DateTimeOffset.UtcNow, snapshot.Version, rcloneVersion = EngineLocator.Version, snapshot.WinFspInstalled, snapshot.EngineAvailable, snapshot.Elevated, mounts = snapshot.Mounts.Select((s, index) => new { index, s.Phase, s.Uploads, s.ObservedAt, s.Queued, s.Uploading, s.CacheBytes, s.RecoveryRequired }) };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Wire.Json), token);
        return new() { Success = true, Message = "Redacted diagnostics saved locally. Review before sharing.", ExportPath = path };
    }

    public void Dispose()
    {
        foreach (var worker in _workers.Values) worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _workers.Clear(); // Mount intents deliberately remain uncertain on process exit.
        _operations.Dispose();
    }
}