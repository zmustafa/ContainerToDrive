using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ContainerToDrive.Core;

namespace ContainerToDrive.Windows;

public sealed class ControllerClient
{
    private const string ControllerName = "ContainerToDrive.Controller.exe";
    private readonly string _dataRoot;
    private readonly string _pipeName;
    private readonly Lazy<string> _executable;

    public ControllerClient(string dataRoot)
    {
        _dataRoot = AppPaths.NormalizeDataRoot(dataRoot);
        _pipeName = AppPaths.PipeName(_dataRoot);
        // Cache a successfully selected trust anchor, not a transient missing-artifact failure.
        _executable = new Lazy<string>(ResolveExecutable, LazyThreadSafetyMode.PublicationOnly);
    }

    public async Task EnsureStartedAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var executable = _executable.Value;
            if (await ProbeAsync(executable, deadline.Token).ConfigureAwait(false)) return;
            if (AppPaths.IsElevated) throw new UnauthorizedAccessException("An elevated process cannot launch the controller. Open the application normally.");

            AppPaths.SecureDirectory(_dataRoot);
            var runtime = Path.Combine(_dataRoot, "runtime");
            AppPaths.SecureDirectory(runtime);
            if (AppPaths.LegacyControllerIsRunning(_dataRoot))
                throw new InvalidOperationException("A legacy BlobToDrive controller is still running for this data root. Close it through its normal disconnect and exit workflow before opening ContainerToDrive.");
            var startupLock = Path.Combine(runtime, "startup-" + AppPaths.SessionKey(_dataRoot) + ".lock");
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var held = TryStartupLock(startupLock);
                if (held is not null)
                {
                    // A concurrent launch may have won between our first probe and the file lock.
                    if (await ProbeAsync(executable, deadline.Token).ConfigureAwait(false)) return;
                    deadline.Token.ThrowIfCancellationRequested();
                    AppPaths.RejectReparsePoints(executable);
                    if (AppPaths.IsElevated) throw new UnauthorizedAccessException("An elevated process cannot launch the controller.");
                    var start = new ProcessStartInfo
                    {
                        FileName = executable,
                        WorkingDirectory = Path.GetDirectoryName(executable)!,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    start.ArgumentList.Add("--data-root");
                    start.ArgumentList.Add(_dataRoot);
                    // This launch has no secret inputs, shell, PATH search, or elevation verb.
                    using var launched = Process.Start(start) ?? throw new IOException("The controller could not be started.");
                    while (!await ProbeAsync(executable, deadline.Token).ConfigureAwait(false))
                        await Task.Delay(150, deadline.Token).ConfigureAwait(false);
                    return;
                }

                // Startup losers authenticate the winner rather than launch another copy.
                if (await ProbeAsync(executable, deadline.Token).ConfigureAwait(false)) return;
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The controller did not become available in time.");
        }
        catch (Win32Exception)
        {
            throw new IOException("The controller could not be started.");
        }
    }

    public async Task<Response> SendAsync(Request request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != Wire.Version) throw new InvalidDataException("Unsupported controller protocol version.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Operation == "RelocateData" ? TimeSpan.FromMinutes(31) : TimeSpan.FromSeconds(90));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var executable = _executable.Value;
            using var pipe = CreateClient();
            await pipe.ConnectAsync(5000, deadline.Token).ConfigureAwait(false);
            ProcessIdentity.ValidateServer(pipe, executable);
            // Authenticate this specific connection, not just an earlier startup probe, before the SAS.
            await PipeWire.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            return await PipeWire.ReadAsync<Response>(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The controller request timed out. Its outcome may be unknown; refresh before retrying.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The controller connection timed out.");
        }
        // No automatic replay: a broken pipe after a write does not mean a mutation was not committed.
    }

    private NamedPipeClientStream CreateClient() => new(".", _pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);

    private async Task<bool> ProbeAsync(string executable, CancellationToken token)
    {
        using var pipe = CreateClient();
        try { await pipe.ConnectAsync(300, token).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 2 or 231) { return false; }

        // Authentication failure never falls through to launching over an unexpected pipe owner.
        ProcessIdentity.ValidateServer(pipe, executable);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await PipeWire.WriteAsync(pipe, new Request { Operation = "Status" }, handshake.Token).ConfigureAwait(false);
            var response = await PipeWire.ReadAsync<Response>(pipe, handshake.Token).ConfigureAwait(false);
            if (!response.Success) throw new IOException("The controller is not ready.");
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A verified but stuck server is not permission to start another controller.
            throw new TimeoutException("The controller did not answer its readiness check.");
        }
    }

    private static FileStream? TryStartupLock(string path)
    {
        AppPaths.RejectReparsePoints(path);
        FileStream held;
        try
        {
            held = new FileInfo(path).Create(FileMode.OpenOrCreate, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, AppPaths.FileAcl());
        }
        catch (IOException exception) when (AppPaths.IsSharingViolation(exception)) { return null; }
        try
        {
            var owner = held.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner?.Value != AppPaths.CurrentSid && owner?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("The controller startup lock has an unexpected owner.");
            held.SetAccessControl(AppPaths.FileAcl());
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private static string ResolveExecutable()
    {
        // Trust is anchored to this application's directory, never the working directory or PATH.
        var applicationDirectory = AppPaths.NormalizeDataRoot(AppContext.BaseDirectory);
        var packaged = Path.Combine(applicationDirectory, "controller", ControllerName);
        if (TrustedFileExists(packaged) && TrustedFileExists(Path.ChangeExtension(packaged, ".dll"))) return packaged;
        packaged = Path.Combine(applicationDirectory, ControllerName);
        if (TrustedFileExists(packaged) && TrustedFileExists(Path.ChangeExtension(packaged, ".dll"))) return packaged;

        for (var directory = new DirectoryInfo(applicationDirectory); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(marker)) continue;
            AppPaths.RejectReparsePoints(marker);
            var output = Path.Combine(directory.FullName, "artifacts", "bin", "ContainerToDrive.Controller");
            // Match the desktop's artifact configuration if known, otherwise the library build configuration.
            var relative = Path.GetRelativePath(directory.FullName, applicationDirectory).Split(Path.DirectorySeparatorChar);
            var preferred = relative.Contains("release", StringComparer.OrdinalIgnoreCase) ? "release" :
                relative.Contains("debug", StringComparer.OrdinalIgnoreCase) ? "debug" : DefaultConfiguration;
            foreach (var configuration in new[] { preferred, preferred == "debug" ? "release" : "debug" })
            {
                var candidate = Path.Combine(output, configuration, ControllerName);
                if (TrustedFileExists(candidate)) return candidate;
            }
            break;
        }
        throw new FileNotFoundException("The trusted controller executable was not found beside the application or in this checkout's build artifacts.");
    }

    private static bool TrustedFileExists(string path)
    {
        AppPaths.RejectReparsePoints(path);
        return AppPaths.FileExists(path);
    }

#if DEBUG
    private const string DefaultConfiguration = "debug";
#else
    private const string DefaultConfiguration = "release";
#endif
}