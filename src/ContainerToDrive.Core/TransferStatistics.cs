namespace ContainerToDrive.Core;

public sealed record TransferBucket(DateTimeOffset Start, long Bytes, long CompletedTransfers, long Errors, long Samples);

public sealed record TransferHistory
{
    public int SchemaVersion { get; init; } = 1;
    public Guid SessionId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public TransferCounters Previous { get; init; } = new(0, 0, 0, 0);
    public long Bytes { get; init; }
    public long CompletedTransfers { get; init; }
    public long Errors { get; init; }
    public List<TransferBucket> Days { get; init; } = [];
    public List<TransferBucket> Hours { get; init; } = [];

    public TransferHistory Record(Guid sessionId, TransferCounters counters, DateTimeOffset observedAt)
    {
        if (sessionId == Guid.Empty || counters.Bytes < 0 || counters.CompletedTransfers < 0 || counters.Errors < 0 ||
            !double.IsFinite(counters.BytesPerSecond) || counters.BytesPerSecond < 0)
            throw new ArgumentException("Invalid transfer observation.");
        observedAt = observedAt.ToUniversalTime();
        if (ObservedAt is { } previousTime && observedAt <= previousTime) return this;
        var sameSession = sessionId == SessionId;
        var bytes = Delta(counters.Bytes, Previous.Bytes, sameSession);
        var transfers = Delta(counters.CompletedTransfers, Previous.CompletedTransfers, sameSession);
        var errors = Delta(counters.Errors, Previous.Errors, sameSession);
        var day = new DateTimeOffset(observedAt.UtcDateTime.Date, TimeSpan.Zero);
        var hour = day.AddHours(observedAt.Hour);
        return this with
        {
            SessionId = sessionId, StartedAt = StartedAt ?? observedAt, ObservedAt = observedAt, Previous = counters,
            Bytes = checked(Bytes + bytes), CompletedTransfers = checked(CompletedTransfers + transfers), Errors = checked(Errors + errors),
            Days = Append(Days, day, day.AddDays(-365), bytes, transfers, errors),
            Hours = Append(Hours, hour, hour.AddHours(-47), bytes, transfers, errors)
        };
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Bytes < 0 || CompletedTransfers < 0 || Errors < 0 ||
            Previous is null || Previous.Bytes < 0 || Previous.CompletedTransfers < 0 || Previous.Errors < 0 ||
            !double.IsFinite(Previous.BytesPerSecond) || Previous.BytesPerSecond < 0 ||
            Days is null || Hours is null || Days.Count > 366 || Hours.Count > 48 || StartedAt > ObservedAt ||
            Days.Concat(Hours).Any(bucket => bucket is null || bucket.Bytes < 0 || bucket.CompletedTransfers < 0 || bucket.Errors < 0 || bucket.Samples < 1) ||
            Days.Select(bucket => bucket.Start).Distinct().Count() != Days.Count || Hours.Select(bucket => bucket.Start).Distinct().Count() != Hours.Count)
            throw new InvalidDataException("Saved transfer history is unreadable.");
    }

    private static long Delta(long current, long previous, bool sameSession) => sameSession && current >= previous ? current - previous : current;

    private static List<TransferBucket> Append(List<TransferBucket> buckets, DateTimeOffset start, DateTimeOffset cutoff, long bytes, long transfers, long errors)
    {
        var retained = buckets.Where(bucket => bucket.Start >= cutoff).ToList();
        var index = retained.FindIndex(bucket => bucket.Start == start);
        if (index < 0) retained.Add(new(start, bytes, transfers, errors, 1));
        else
        {
            var old = retained[index];
            retained[index] = old with { Bytes = checked(old.Bytes + bytes), CompletedTransfers = checked(old.CompletedTransfers + transfers),
                Errors = checked(old.Errors + errors), Samples = checked(old.Samples + 1) };
        }
        return retained;
    }
}

public sealed record TransferSummary
{
    public Guid ProfileId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public long Bytes { get; init; }
    public long CompletedTransfers { get; init; }
    public long Errors { get; init; }
    public double? BytesPerSecond { get; init; }
    public bool Unavailable { get; init; }
}

public sealed record TransferShare(Guid ProfileId, long Bytes);
public sealed record TransferReport
{
    public List<TransferBucket> Buckets { get; init; } = [];
    public List<TransferShare> Shares { get; init; } = [];
    public long Bytes { get; init; }
    public long CompletedTransfers { get; init; }
    public long Errors { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public int UnavailableProfiles { get; init; }
}