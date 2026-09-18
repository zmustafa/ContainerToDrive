using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Rclone;

public sealed class RcloneWorker : IAsyncDisposable
{
    private readonly Profile _profile;
    private readonly string _runtime;
    private readonly string _cache;
    private readonly Action<string> _event;
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private WorkerJob? _job;
    private HttpClient? _http;
    private FileStream? _cacheLock;
    private Task? _logReader;
    private Task? _outputReader;
    private string _lastError = "";
    private bool _mounted;
    public Guid SessionId { get; } = Guid.NewGuid();
    public bool Alive => _process is { HasExited: false };

    public RcloneWorker(Profile profile, string runtime, string cache, Action<string>? onEvent = null)
    { _profile = profile; _runtime = runtime; _cache = cache; _event = onEvent ?? (_ => { }); }

    public Task StartAsync(string sas, CancellationToken token) =>
        StartAsync(CredentialSubmission.ContainerSas(sas, _profile.CredentialRevision), token);

    public async Task StartAsync(CredentialSubmission credential, CancellationToken token)
    {
        var credentialInfo = Validation.ValidateCredential(_profile, credential);
        AppPaths.SecureDirectory(_runtime);
        AppPaths.SecureDirectory(_cache);
        _cacheLock = new FileStream(Path.Combine(_cache, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var drive = new DriveInfo(Path.GetPathRoot(_cache)!);
        if (drive.DriveFormat != "NTFS" || drive.DriveType != DriveType.Fixed)
            throw new InvalidOperationException("The cache must be on a local fixed NTFS volume.");
        if (!_profile.ReadOnly && drive.AvailableFreeSpace < _profile.MinFreeBytes)
            throw new IOException("Not enough free disk space for a writable cache.");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ContainerToDrive local control", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var certPath = Path.Combine(_runtime, "control-cert.pem");
        var keyPath = Path.Combine(_runtime, "control-key.pem");
        await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem(), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem(), token).ConfigureAwait(false);
        var expectedCertificate = certificate.GetCertHash(HashAlgorithmName.SHA256);
        var port = FreePort();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null &&
            CryptographicOperations.FixedTimeEquals(expectedCertificate, presented.GetCertHash(HashAlgorithmName.SHA256)) &&
            DateTime.UtcNow >= presented.NotBefore.ToUniversalTime() && DateTime.UtcNow <= presented.NotAfter.ToUniversalTime();
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ctd:" + password)));
        var start = new ProcessStartInfo(EngineLocator.FindVerified()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = _runtime };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["RCLONE_RC_PASS"] = password;
        foreach (var arg in new[] { "rcd", "--config", "NUL", "--ask-password=false", "--rc-addr", $"127.0.0.1:{port}", "--rc-user", "ctd", "--rc-cert", certPath, "--rc-key", keyPath, "--rc-min-tls-version", "tls1.2", "--cache-dir", _cache, "--temp-dir", _runtime, "--use-json-log", "--log-level", "NOTICE", "--transfers", "2", "--buffer-size", "8M" }) start.ArgumentList.Add(arg);
        _job = new WorkerJob();
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the engine.");
        try { _job.Assign(_process); } catch { _process.Kill(true); throw; }
        _logReader = DrainAsync(_process.StandardError, _lifetime.Token);
        _outputReader = DrainAsync(_process.StandardOutput, _lifetime.Token);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
        ready.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (!Alive) throw new InvalidOperationException("Engine exited during startup. " + _lastError);
            try { await CallAsync("rc/noopauth", new { }, ready.Token).ConfigureAwait(false); break; }
            catch (HttpRequestException) { await Task.Delay(100, ready.Token).ConfigureAwait(false); }
        }
        var version = await CallAsync("core/version", new { }, token).ConfigureAwait(false);
        if (version.GetProperty("version").GetString() != "v" + EngineLocator.Version) throw new InvalidOperationException("Unexpected running engine version.");
        await CallAsync("config/create", new
        {
            name = _profile.RemoteName,
            type = "azureblob",
            parameters = BackendParameters(_profile, credentialInfo),
            opt = new { nonInteractive = true }
        }, token).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> BackendParameters(Profile profile, CredentialInfo credential)
    {
        if (credential.Kind != profile.AuthenticationKind)
            throw new ArgumentException("The credential type does not match the profile.");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["no_check_container"] = "true",
            ["directory_markers"] = (!profile.ReadOnly).ToString().ToLowerInvariant(),
            ["env_auth"] = "false",
            ["use_az"] = "false"
        };
        switch (credential.Kind)
        {
            case AuthenticationKind.ContainerSas:
            case AuthenticationKind.MicrosoftEntra:
                parameters["sas_url"] = credential.Secret;
                break;
            case AuthenticationKind.AccountKey:
                parameters["account"] = Validation.AccountName(profile);
                parameters["key"] = credential.Secret;
                break;
            default:
                throw new ArgumentException("Unsupported authentication method.");
        }
        return parameters;
    }

    public async Task ValidateAsync(CancellationToken token) => await CallAsync("operations/list", new { fs = _profile.RemotePath, remote = "", opt = new { recurse = false, noMimeType = true } }, token);

    public async Task MountAsync(CancellationToken token)
    {
        if (AppPaths.IsElevated) throw new InvalidOperationException("Mount from a normal Windows session, not an elevated application.");
        if (!AppPaths.WinFspInstalled) throw new InvalidOperationException("Install the official WinFsp prerequisite first.");
        if (DriveInfo.GetDrives().Any(d => d.Name.StartsWith(_profile.DriveLetter + ":", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected drive letter is in use.");
        var types = await CallAsync("mount/types", new { }, token);
        var available = types.GetProperty("mountTypes").EnumerateArray().Select(x => x.GetString()).ToArray();
        var type = available.Contains("mount") ? "mount" : available.Contains("cmount") ? "cmount" : throw new InvalidOperationException("The engine has no supported Windows mount implementation.");
        await CallAsync("mount/mount", new
        {
            fs = _profile.RemotePath, mountPoint = _profile.DriveLetter + ":", mountType = type,
            mountOpt = new { NetworkMode = true, VolumeName = "ContainerToDrive-" + _profile.Id.ToString("N")[..8], AttrTimeout = "1s", ExtraOptions = new[] { "FileSecurity=D:P(A;;FA;;;" + AppPaths.CurrentSid + ")" } },
            vfsOpt = new { ReadOnly = _profile.ReadOnly, CacheMode = "full", CacheMaxSize = _profile.CacheMaxBytes, CacheMinFreeSpace = _profile.MinFreeBytes, CacheMaxAge = "24h", DirCacheTime = "30s", PollInterval = "0s", WriteBack = "5s" }
        }, token);
        _mounted = true;
    }

    public async Task<MountStatus> ObserveAsync(CancellationToken token)
    {
        if (!Alive) return new() { ProfileId = _profile.Id, Phase = MountPhase.Faulted, Message = "Engine stopped. Recovery inspection may be required.", RecoveryRequired = !_profile.ReadOnly };
        try
        {
            var stats = await CallAsync("vfs/stats", new { fs = _profile.RemotePath }, token);
            var cache = stats.GetProperty("diskCache");
            int queued = cache.GetProperty("uploadsQueued").GetInt32(), uploading = cache.GetProperty("uploadsInProgress").GetInt32(), errors = cache.GetProperty("erroredFiles").GetInt32();
            var queue = await CallAsync("vfs/queue", new { fs = _profile.RemotePath }, token);
            var items = new List<UploadItem>();
            if (queue.TryGetProperty("queue", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray().Take(100)) items.Add(new(Redaction.Clean(item.GetProperty("name").GetString()), item.GetProperty("size").GetInt64(), item.GetProperty("uploading").GetBoolean(), item.GetProperty("tries").GetInt32()));
            var state = _profile.ReadOnly ? UploadState.NotApplicable : errors > 0 ? UploadState.Failed : uploading > 0 ? UploadState.Uploading : queued > 0 ? UploadState.Pending : UploadState.NoQueuedUploadsReported;
            return new() { ProfileId = _profile.Id, Phase = MountPhase.Mounted, Uploads = state, ObservedAt = DateTimeOffset.UtcNow, CacheBytes = cache.GetProperty("bytesUsed").GetInt64(), Queued = queued, Uploading = uploading, Queue = items, Message = cache.GetProperty("outOfSpace").GetBoolean() ? "Local cache disk is full. Do not delete pending data." : _profile.ReadOnly ? "Connected - read-only" : state.ToString() };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
        { return new() { ProfileId = _profile.Id, Phase = MountPhase.Mounted, Message = "Upload status unavailable; do not assume changes reached Azure.", Uploads = UploadState.Unknown }; }
    }

    public Task<JsonElement> RefreshAsync(CancellationToken token) => CallAsync("vfs/refresh", new { fs = _profile.RemotePath, recursive = false }, token);
    public async Task<TransferCounters> ReadTransferCountersAsync(CancellationToken token)
    {
        var stats = await CallAsync("core/stats", new { shortOutput = true }, token);
        var counters = new TransferCounters(stats.GetProperty("bytes").GetInt64(), stats.GetProperty("transfers").GetInt64(),
            stats.GetProperty("errors").GetInt64(), stats.GetProperty("speed").GetDouble());
        if (counters.Bytes < 0 || counters.CompletedTransfers < 0 || counters.Errors < 0 ||
            !double.IsFinite(counters.BytesPerSecond) || counters.BytesPerSecond < 0)
            throw new InvalidDataException("The engine returned invalid transfer counters.");
        return counters;
    }

    public async Task UnmountAsync(CancellationToken token)
    {
        if (_mounted && Alive) { await CallAsync("mount/unmount", new { mountPoint = _profile.DriveLetter + ":" }, token); _mounted = false; }
    }

    public Task<JsonElement> CallAsync(string endpoint, object body, CancellationToken token) =>
        // Issue socket operations on the pool, never on a short-lived caller
        // thread (Windows cancels outstanding I/O when that thread exits).
        // This changes execution context, not retry/replay semantics.
        Task.Run(() => CallCoreAsync(endpoint, body, token), token);

    private async Task<JsonElement> CallCoreAsync(string endpoint, object body, CancellationToken token)
    {
        if (_http is null) throw new InvalidOperationException("Engine is not connected.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(body, Wire.Json), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Engine operation {endpoint} failed (HTTP {(int)response.StatusCode}). Credentials or permissions may need attention.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bounded.Length + length > 8 * 1024 * 1024) throw new InvalidDataException("The engine response exceeds the supported size. Select a narrower prefix.");
            bounded.Write(buffer, 0, length);
        }
        using var doc = JsonDocument.Parse(bounded.ToArray());
        return doc.RootElement.Clone();
    }

    private async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[4096]; var line = new StringBuilder(); bool discard = false;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (!discard)
                        {
                            try { using var doc = JsonDocument.Parse(line.ToString()); if (doc.RootElement.TryGetProperty("level", out var level) && level.GetString() is "error" or "fatal") { _lastError = "The engine reported an error; check credentials, network, and cache permissions."; _event("EngineError"); } }
                            catch (JsonException) { /* Do not persist unparseable, potentially sensitive output. */ }
                        }
                        line.Clear(); discard = false;
                    }
                    else if (line.Length < 16384 && !discard) line.Append(buffer[i]); else discard = true;
                }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    public async ValueTask DisposeAsync()
    {
        if (Alive && _http is not null)
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await CallAsync("core/quit", new { }, timeout.Token); } catch (Exception) { /* Job disposal terminates only our owned worker. */ }
        }
        _job?.Dispose();
        if (_process is not null) { try { if (!_process.HasExited) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } }
        _lifetime.Cancel();
        foreach (var task in new[] { _logReader, _outputReader }) if (task is not null) { try { await task; } catch (Exception) { } }
        _http?.Dispose(); _process?.Dispose(); _cacheLock?.Dispose(); _lifetime.Dispose();
        foreach (var name in new[] { "control-key.pem", "control-cert.pem" }) { try { File.Delete(Path.Combine(_runtime, name)); } catch (IOException) { } }
    }
}