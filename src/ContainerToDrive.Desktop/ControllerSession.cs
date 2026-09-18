using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Desktop;

// All callers are on the WPF dispatcher. The gate also serializes dialogs and polling.
internal sealed class ControllerSession(ControllerClient client, bool allowStart = true)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    public SessionLog Log { get; } = new();
    public event Action<AppSnapshot>? SnapshotReceived;
    public event Action? Unavailable;

    public async Task<Response> SendAsync(Request request, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(request.Operation == "RelocateData" ? TimeSpan.FromMinutes(31)
                : request.Operation is "Status" or "Statistics" ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(90));
            // An elevated desktop may inspect an existing controller, but must not
            // start an elevated controller that could attempt automatic mounting.
            if (!_started && allowStart)
            {
                await client.EnsureStartedAsync(timeout.Token);
                _started = true;
            }

            var response = await client.SendAsync(request, timeout.Token);
            Log.RecordResponse(request.Operation, request.RequestId, response.Success && (request.Operation != "Status" || response.Snapshot is not null));
            if (response.Snapshot is { } snapshot) SnapshotReceived?.Invoke(snapshot);
            return response;
        }
        catch (Exception exception)
        {
            Log.RecordFailure(request.Operation, request.RequestId, exception is OperationCanceledException);
            // Reconnect through the controller's own single-instance startup contract.
            _started = false;
            Unavailable?.Invoke();
            throw;
        }
        finally { _gate.Release(); }
    }
}