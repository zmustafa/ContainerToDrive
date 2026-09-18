using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ContainerToDrive.Desktop;

public sealed record SessionLogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message, Guid? RequestId)
{
    public string Context => RequestId is { } requestId ? $"{Source} | Request {requestId:N}" : Source;
}

public sealed class SessionLog : INotifyPropertyChanged
{
    public const int Capacity = 500;
    private readonly ObservableCollection<SessionLogEntry> _entries = [];
    private bool? _backendAvailable;

    public SessionLog()
    {
        Entries = new(_entries);
        Add("Info", "Desktop", "Application session started.");
    }

    public ReadOnlyObservableCollection<SessionLogEntry> Entries { get; }
    public bool HasEntries => _entries.Count != 0;
    public bool IsEmpty => !HasEntries;
    public string Summary => $"{_entries.Count} session event{(_entries.Count == 1 ? "" : "s")}";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RecordResponse(string operation, Guid requestId, bool success)
    {
        RecordConnection(operation != "Status" || success);
        if (operation is "Status" or "Statistics") return;
        Add(success ? "Info" : "Error", "Backend", OperationName(operation) + (success ? " completed." : " failed."), requestId);
    }

    internal void RecordFailure(string operation, Guid requestId, bool cancelled)
    {
        RecordConnection(false);
        if (operation is "Status" or "Statistics") return;
        Add(cancelled ? "Warning" : "Error", "Backend", OperationName(operation) +
            (cancelled ? " wait cancelled or timed out; the controller action may still complete." : " could not reach the controller."), requestId);
    }

    public void Clear()
    {
        _entries.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void RecordConnection(bool available)
    {
        if (_backendAvailable == available) return;
        _backendAvailable = available;
        Add(available ? "Info" : "Warning", "Backend", available ? "Backend connection established." : "Backend connection unavailable; running drive state is unknown.");
    }

    private void Add(string level, string source, string message, Guid? requestId = null)
    {
        _entries.Insert(0, new(DateTimeOffset.Now, level, source, message, requestId));
        if (_entries.Count > Capacity) _entries.RemoveAt(_entries.Count - 1);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string OperationName(string operation) => operation switch
    {
        "Save" => "Save connection",
        "Clone" => "Clone connection",
        "Validate" => "Test connection access",
        "DiscoverContainers" => "Discover containers",
        "Mount" => "Mount drive",
        "Disconnect" => "Disconnect drive",
        "Refresh" => "Refresh directory",
        "Renew" => "Renew credential",
        "Remove" => "Remove connection",
        "Export" => "Export diagnostics",
        "RelocateData" => "Change application data folder",
        "Shutdown" => "Stop backend",
        "TestCapabilities" => "Test capabilities",
        _ => "Controller request"
    };
}