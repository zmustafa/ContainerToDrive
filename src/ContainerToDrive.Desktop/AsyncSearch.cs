namespace ContainerToDrive.Desktop;

internal sealed class AsyncSearch : IDisposable
{
    private Func<string, CancellationToken, Task<IReadOnlyList<object>>>? _source;
    private CancellationTokenSource? _pending;
    private int _generation;
    private bool _disposed;

    internal IReadOnlyList<object> Items { get; private set; } = [];
    internal bool IsLoading { get; private set; }
    internal bool HasError { get; private set; }
    internal string Status { get; private set; } = "";
    internal event Action? Changed;

    internal void SetSource(Func<string, CancellationToken, Task<IReadOnlyList<object>>>? source)
    {
        StopPending();
        _source = source;
        Items = [];
        Status = "";
        HasError = false;
        Changed?.Invoke();
    }

    internal async Task RunAsync(string query, TimeSpan? debounce = null)
    {
        if (_disposed || _source is null) return;
        StopPending();
        var generation = _generation;
        var source = _source;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        _pending = cancellation;
        Items = [];
        HasError = false;
        Status = "Searching...";
        IsLoading = true;
        Changed?.Invoke();
        try
        {
            await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(250), cancellation.Token);
            var items = await source(query.Trim(), cancellation.Token);
            if (_disposed || generation != _generation) return;
            cancellation.Token.ThrowIfCancellationRequested();
            Items = items;
            Status = items.Count == 0 ? "No matching results" : "";
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) Status = "Search cancelled";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (generation == _generation)
            {
                HasError = true;
                Status = "Results could not be loaded. Refresh to retry.";
            }
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                _pending = null;
                IsLoading = false;
                Changed?.Invoke();
            }
        }
    }

    internal void Cancel()
    {
        StopPending();
        Status = "Search cancelled";
        Changed?.Invoke();
    }

    private void StopPending()
    {
        _generation++;
        var pending = _pending;
        _pending = null;
        pending?.Cancel();
        IsLoading = false;
    }

    public void Dispose()
    {
        _disposed = true;
        StopPending();
        _source = null;
    }

    internal static Func<string, CancellationToken, Task<IReadOnlyList<object>>> Cached<T>(Func<CancellationToken, Task<IReadOnlyList<T>>> load)
    {
        IReadOnlyList<T>? cached = null;
        return async (query, token) =>
        {
            if (cached is null)
            {
                var items = await load(token);
                token.ThrowIfCancellationRequested();
                cached = items;
            }
            token.ThrowIfCancellationRequested();
            return cached.Where(item => (item?.ToString() ?? "").Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).Cast<object>().ToArray();
        };
    }
}