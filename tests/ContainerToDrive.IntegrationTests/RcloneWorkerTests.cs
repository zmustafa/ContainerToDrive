using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Rclone;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class RcloneWorkerTests
{
    [Fact]
    public void BackendParametersAreMutuallyExclusiveAndDisableAmbientAzureAuthentication()
    {
        var profile = SyntheticCredential.Profile();
        var sasSecret = SyntheticCredential.Create().Url;
        var sas = RcloneWorker.BackendParameters(profile,
            new CredentialInfo(AuthenticationKind.ContainerSas, sasSecret, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(new[] { "directory_markers", "env_auth", "no_check_container", "sas_url", "use_az" }, sas.Keys.Order().ToArray());
        Assert.True(string.Equals(sasSecret, sas["sas_url"], StringComparison.Ordinal), "The synthetic SAS parameter differs; secret text is not printed.");
        Assert.Equal("false", sas["env_auth"]);
        Assert.Equal("false", sas["use_az"]);

        var keySecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var keyProfile = profile with { AuthenticationKind = AuthenticationKind.AccountKey };
        var accountKey = RcloneWorker.BackendParameters(keyProfile,
            new CredentialInfo(AuthenticationKind.AccountKey, keySecret, null));
        Assert.Equal(new[] { "account", "directory_markers", "env_auth", "key", "no_check_container", "use_az" }, accountKey.Keys.Order().ToArray());
        Assert.Equal(Validation.AccountName(keyProfile), accountKey["account"]);
        Assert.True(string.Equals(keySecret, accountKey["key"], StringComparison.Ordinal), "The synthetic key parameter differs; secret text is not printed.");
        Assert.False(accountKey.ContainsKey("sas_url"));

        var entraProfile = profile with
        {
            AuthenticationKind = AuthenticationKind.MicrosoftEntra,
            TenantId = "11111111-1111-1111-1111-111111111111",
            SubscriptionId = "22222222-2222-2222-2222-222222222222",
            ResourceGroupName = "synthetic-rg"
        };
        var entra = RcloneWorker.BackendParameters(entraProfile,
            new CredentialInfo(AuthenticationKind.MicrosoftEntra, sasSecret, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(new[] { "directory_markers", "env_auth", "no_check_container", "sas_url", "use_az" }, entra.Keys.Order().ToArray());
        Assert.False(entra.ContainsKey("account"));
        Assert.False(entra.ContainsKey("key"));
    }

    [Fact]
    public async Task ActualWorkerReportsNumericTransferCountersWithoutExposingTransferNames()
    {
        await WithWorkerAsync(async (running, token) =>
        {
            Assert.NotEqual(Guid.Empty, running.Worker.SessionId);
            var counters = await running.Worker.ReadTransferCountersAsync(token);
            Assert.Equal(new TransferCounters(0, 0, 0, 0), counters);
            Assert.Equal(counters, await running.Worker.ReadTransferCountersAsync(token));
            Assert.Equal(new[] { "bytes", "bytesPerSecond", "completedTransfers", "errors" },
                JsonSerializer.SerializeToElement(counters, Wire.Json).EnumerateObject().Select(property => property.Name).Order().ToArray());
            var source = Path.Combine(running.Runtime, "statistics-source");
            var destination = Path.Combine(running.Runtime, "statistics-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(source, "sample.bin"), new byte[8192]);
            await running.Worker.CallAsync("operations/copyfile", new { srcFs = source, srcRemote = "sample.bin", dstFs = destination, dstRemote = "sample.bin" }, token);
            var transferred = await running.Worker.ReadTransferCountersAsync(token);
            Assert.Equal(8192, transferred.Bytes);
            Assert.Equal(1, transferred.CompletedTransfers);
            Assert.Equal(0, transferred.Errors);
            Assert.Equal(8192, new FileInfo(Path.Combine(destination, "sample.bin")).Length);
        });
    }

    [Fact]
    public async Task ActualWorkerUsesPinnedEngineMemoryOnlyConfigAndSecretFreeRuntimeAndCommandLine()
    {
        await WithWorkerAsync(async (running, token) =>
        {
            var version = await running.Worker.CallAsync("core/version", new { }, token);
            Assert.Equal("v" + EngineLocator.Version, version.GetProperty("version").GetString());
            Assert.Equal("windows", version.GetProperty("os").GetString());
            Assert.Equal("amd64", version.GetProperty("arch").GetString());
            Assert.False(version.GetProperty("isBeta").GetBoolean());

            var paths = await running.Worker.CallAsync("config/paths", new { }, token);
            // Pinned v1.75.1 fs/config.SetConfigPath("NUL") stores "", and rcPaths returns it as-is.
            // Do not accept an arbitrary filename, "memory", or a missing/null property as success.
            Assert.Equal("", paths.GetProperty("config").GetString());
            AssertPath(running.Cache, paths.GetProperty("cache").GetString());
            AssertPath(running.Runtime, paths.GetProperty("temp").GetString());
            await AssertOnlyOwnRemoteAsync(running, token);

            var commandLine = OwnedProcessCommandLine.Read(running.Process);
            LocalTestRoot.AssertNoPlaintext(Encoding.UTF8.GetBytes(commandLine), running.Credential);
            // Avoid substring assertions that would dump the entire process command line on failure.
            var memoryConfigArgument = commandLine.Contains("--config NUL", StringComparison.Ordinal);
            var passwordArgument = commandLine.Contains("--rc-pass", StringComparison.OrdinalIgnoreCase);
            Assert.True(memoryConfigArgument,
                "The worker was not launched with explicit memory-only configuration.");
            Assert.False(passwordArgument,
                "An RC password option must not appear in the process command line.");
            Assert.True(File.Exists(Path.Combine(running.Runtime, "control-cert.pem")));
            Assert.True(File.Exists(Path.Combine(running.Runtime, "control-key.pem")));
            running.Root.AssertNoPlaintext([running.Credential], Path.Combine(running.Cache, ".owner.lock"));
        });
    }

    [Theory]
    [InlineData("rc/noopauth")]
    [InlineData("core/version")]
    [InlineData("config/paths")]
    public async Task ControlEndpointRejectsMissingAndIncorrectAuthentication(string endpoint)
    {
        await WithWorkerAsync(async (running, token) =>
        {
            var allowed = await running.Worker.CallAsync(endpoint, new { }, token);
            Assert.Equal(JsonValueKind.Object, allowed.ValueKind);
            var address = ReadLoopbackAddressOnly(running.Worker);
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
                Credentials = null,
                // TEST LOCAL ONLY: bypass trust/pinning to reach HTTP authentication with an
                // unauthenticated client. Never copied to the production handler or global TLS policy.
                // No SAS or genuine RC credentials are sent. Only this exact IPv4 loopback port is allowed.
                ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                    certificate is not null && request.RequestUri is { } uri &&
                    uri.Scheme == Uri.UriSchemeHttps && uri.Host == "127.0.0.1" && uri.Port == address.Port
            };
            using var anonymous = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            })
            using (var response = await anonymous.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("ctd:deliberately-invalid-test-password")));
                using var response = await anonymous.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            // A network/TLS failure is not evidence of authentication rejection; require success again.
            Assert.Equal(JsonValueKind.Object, (await running.Worker.CallAsync(endpoint, new { }, token)).ValueKind);
        });
    }

    [Fact]
    public async Task DisposingOneOwnedWorkerLeavesAnotherOwnedWorkerAndItsConfigIntact()
    {
        await WithWorkerAsync(async (first, token) =>
        {
            await WithWorkerAsync(async (second, secondToken) =>
            {
                Assert.NotEqual(first.Process.Id, second.Process.Id);
                Assert.NotEqual(ReadLoopbackAddressOnly(first.Worker), ReadLoopbackAddressOnly(second.Worker));
                await AssertOnlyOwnRemoteAsync(first, token);
                await AssertOnlyOwnRemoteAsync(second, secondToken);
            }); // This disposes ONLY the second worker and asserts its exact process has exited.

            Assert.False(first.Process.HasExited);
            Assert.True(first.Worker.Alive);
            var pid = await first.Worker.CallAsync("core/pid", new { }, token);
            Assert.Equal(first.Process.Id, pid.GetProperty("pid").GetInt32());
            await AssertOnlyOwnRemoteAsync(first, token);
        });
    }

    private static async Task AssertOnlyOwnRemoteAsync(RunningWorker running, CancellationToken token)
    {
        // Names only: config/get, config/dump, options/get and job output can expose credentials.
        var result = await running.Worker.CallAsync("config/listremotes", new { }, token);
        Assert.Equal(running.Profile.RemoteName, Assert.Single(result.GetProperty("remotes").EnumerateArray()).GetString());
    }

    private static Uri ReadLoopbackAddressOnly(RcloneWorker worker)
    {
        // Reflection is confined to tests; obtain ONLY BaseAddress, never headers, passwords, or keys.
        var field = typeof(RcloneWorker).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var client = Assert.IsType<HttpClient>(field.GetValue(worker));
        var address = client.BaseAddress;
        Assert.NotNull(address);
        Assert.Equal(Uri.UriSchemeHttps, address.Scheme);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.True(address.IsLoopback);
        Assert.InRange(address.Port, 1, 65535);
        Assert.Equal("/", address.AbsolutePath);
        Assert.Equal("", address.UserInfo);
        Assert.Equal("", address.Query);
        Assert.Equal("", address.Fragment);
        return address;
    }

    private static async Task WithWorkerAsync(Func<RunningWorker, CancellationToken, Task> assertions)
    {
        var root = LocalTestRoot.Create();
        var engine = EngineLocator.FindVerified(); // Missing/hash-mismatched prerequisites MUST fail, not download or skip.
        AssertPath(Path.Combine(root.Workspace, ".tools", "rclone", EngineLocator.Version, "rclone.exe"), engine);
        var credential = SyntheticCredential.Create();
        var store = root.OpenStore();
        var profile = store.Save(SyntheticCredential.Profile(), CredentialSubmission.ContainerSas(credential.Url));
        var cache = store.CachePath(profile.Id);
        var runtime = store.RuntimePath(profile.Id);
        var sentinel = Path.Combine(cache, "synthetic-cache-evidence.bin");
        byte[] evidence = [2, 4, 6, 8, 10];
        File.WriteAllBytes(sentinel, evidence);
        var events = new ConcurrentQueue<string>();
        var worker = new RcloneWorker(profile, runtime, cache, events.Enqueue);
        Process? process = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var startedAfter = DateTime.UtcNow;
        try
        {
            // Real StartAsync + WorkerJob + TLS + config/create, but NEVER ValidateAsync, MountAsync,
            // RefreshAsync, or a cloud URL request. Copy tests use only explicit temporary local paths.
            await worker.StartAsync(credential.Url, deadline.Token);
            Assert.True(worker.Alive);
            var pid = (await worker.CallAsync("core/pid", new { }, deadline.Token)).GetProperty("pid").GetInt32();
            Assert.True(pid > 0);
            Assert.NotEqual(Environment.ProcessId, pid);
            process = Process.GetProcessById(pid);
            // Force opening the handle before disposal, so PID reuse cannot satisfy the exit assertion.
            Assert.False(process.SafeHandle.IsInvalid);
            Assert.False(process.HasExited);
            Assert.True(process.StartTime.ToUniversalTime() >= startedAfter);
            AssertPath(engine, process.MainModule!.FileName);
            using var current = Process.GetCurrentProcess();
            Assert.Equal(current.SessionId, process.SessionId);
            await assertions(new(root, worker, process, profile, credential, cache, runtime), deadline.Token);
        }
        finally
        {
            try
            {
                // No process-name enumeration/kill: production disposal closes its own job only.
                await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                if (process is not null)
                    Assert.True(process.HasExited, "Worker disposal returned with its exact owned process still running.");
                Assert.False(File.Exists(Path.Combine(runtime, "control-key.pem")));
                Assert.False(File.Exists(Path.Combine(runtime, "control-cert.pem")));
                Assert.Equal(evidence, File.ReadAllBytes(sentinel));
                // Prove the exclusive cache owner lock was released, not merely that the PID went away.
                // If startup failed before creating it, don't hide that failure with FileNotFoundException.
                var ownerLock = Path.Combine(cache, ".owner.lock");
                if (process is not null || File.Exists(ownerLock))
                {
                    using var released = new FileStream(ownerLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.Equal(0L, released.Length);
                }
                root.AssertNoPlaintext([credential]); // Includes all backups and the now-readable owner lock.
                foreach (var item in events)
                    LocalTestRoot.AssertNoPlaintext(Encoding.UTF8.GetBytes(item), credential);
            }
            finally { process?.Dispose(); }
        }
    }

    private static void AssertPath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.False(string.IsNullOrWhiteSpace(actual));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)), ignoreCase: true);
    }

    private sealed record RunningWorker(LocalTestRoot Root, RcloneWorker Worker, Process Process,
        Profile Profile, SyntheticCredential Credential, string Cache, string Runtime);
}