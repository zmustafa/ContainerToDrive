using System.Security.Cryptography;
using System.Text.Json;
using ContainerToDrive.Core;

namespace ContainerToDrive.Windows;

public sealed class TransferHistoryStore
{
    private readonly ProtectedSettingsStore _settings;
    private readonly Dictionary<Guid, TransferHistory> _histories = [];
    private readonly HashSet<Guid> _unavailable = [];

    public TransferHistoryStore(string dataRoot) => _settings = new(dataRoot);

    public TransferHistory? Read(Guid profileId)
    {
        if (_histories.TryGetValue(profileId, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            bytes = _settings.Read(Name(profileId));
            var history = bytes is null ? new TransferHistory() : JsonSerializer.Deserialize<TransferHistory>(bytes, Wire.Json)
                ?? throw new InvalidDataException("Saved transfer history is unreadable.");
            history.Validate();
            _histories.Add(profileId, history);
            return history;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _unavailable.Add(profileId);
            return null;
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Record(Guid profileId, Guid sessionId, TransferCounters counters, DateTimeOffset observedAt)
    {
        var history = Read(profileId);
        if (history is null) return;
        byte[]? bytes = null;
        try
        {
            var updated = history.Record(sessionId, counters, observedAt);
            bytes = JsonSerializer.SerializeToUtf8Bytes(updated, Wire.Json);
            _settings.Write(Name(profileId), bytes);
            _histories[profileId] = updated;
            _unavailable.Remove(profileId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or OverflowException)
        { _unavailable.Add(profileId); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void MarkUnavailable(Guid profileId) => _unavailable.Add(profileId);

    public TransferSummary Summary(Guid profileId, bool active)
    {
        var history = Read(profileId);
        return new()
        {
            ProfileId = profileId, StartedAt = history?.StartedAt, ObservedAt = history?.ObservedAt,
            Bytes = history?.Bytes ?? 0, CompletedTransfers = history?.CompletedTransfers ?? 0, Errors = history?.Errors ?? 0,
            BytesPerSecond = active && !_unavailable.Contains(profileId) ? history?.Previous.BytesPerSecond : null,
            Unavailable = _unavailable.Contains(profileId)
        };
    }

    public TransferReport Report(IEnumerable<Guid> profileIds, int days, DateTimeOffset now)
    {
        if (days is not (1 or 7 or 30 or 90 or 365)) throw new ArgumentException("Unsupported statistics period.");
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1 - days);
        var end = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        var buckets = new List<TransferBucket>();
        var shares = new List<TransferShare>();
        DateTimeOffset? first = null, last = null;
        var unavailable = 0;
        foreach (var profileId in profileIds.Distinct())
        {
            var history = Read(profileId);
            if (_unavailable.Contains(profileId)) unavailable++;
            if (history is null) continue;
            if (history.StartedAt is { } started && (first is null || started < first)) first = started;
            if (history.ObservedAt is { } observed && (last is null || observed > last)) last = observed;
            var selected = (days == 1 ? history.Hours : history.Days).Where(bucket => bucket.Start >= start && bucket.Start < end).ToList();
            buckets.AddRange(selected);
            shares.Add(new(profileId, selected.Sum(bucket => bucket.Bytes)));
        }
        return new()
        {
            Buckets = buckets.GroupBy(bucket => bucket.Start).OrderBy(group => group.Key)
                .Select(group => new TransferBucket(group.Key, group.Sum(bucket => bucket.Bytes), group.Sum(bucket => bucket.CompletedTransfers), group.Sum(bucket => bucket.Errors), group.Sum(bucket => bucket.Samples))).ToList(),
            Shares = shares, Bytes = buckets.Sum(bucket => bucket.Bytes), CompletedTransfers = buckets.Sum(bucket => bucket.CompletedTransfers),
            Errors = buckets.Sum(bucket => bucket.Errors), StartedAt = first, ObservedAt = last, UnavailableProfiles = unavailable
        };
    }

    private static string Name(Guid profileId) => "transfers-" + profileId.ToString("N");
}