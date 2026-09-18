using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContainerToDrive.Core;

public static class Wire
{
    public const int Version = 5;
    public const int MaxMessageBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}

public enum MountPhase { Unmounted, Starting, Mounted, DisconnectRequested, Faulted }
public enum UploadState { NotApplicable, Unknown, NoQueuedUploadsReported, Pending, Uploading, Failed }
public enum AuthenticationKind { ContainerSas = 1, AccountKey = 2, MicrosoftEntra = 3 }

public sealed record CredentialSubmission
{
    public AuthenticationKind Kind { get; init; } = AuthenticationKind.ContainerSas;
    public int ExpectedRevision { get; init; }
    public string? SasUrl { get; init; }
    public string? AccountKey { get; init; }

    public static CredentialSubmission ContainerSas(string sasUrl, int expectedRevision = 0) => new()
    {
        Kind = AuthenticationKind.ContainerSas,
        ExpectedRevision = expectedRevision,
        SasUrl = sasUrl
    };

    public static CredentialSubmission AccountKeyCredential(string accountKey, int expectedRevision = 0) => new()
    {
        Kind = AuthenticationKind.AccountKey,
        ExpectedRevision = expectedRevision,
        AccountKey = accountKey
    };

    public static CredentialSubmission MicrosoftEntraSas(string sasUrl, int expectedRevision = 0) => new()
    {
        Kind = AuthenticationKind.MicrosoftEntra,
        ExpectedRevision = expectedRevision,
        SasUrl = sasUrl
    };
}

public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Revision { get; init; }
    public string Name { get; init; } = "Azure container";
    public string Endpoint { get; init; } = "";
    public string Container { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string DriveLetter { get; init; } = "Z";
    public bool ReadOnly { get; init; } = true;
    public bool AutoMount { get; init; }
    public long CacheMaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MinFreeBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public DateTimeOffset? ExpiresAt { get; init; }
    public AuthenticationKind AuthenticationKind { get; init; } = AuthenticationKind.ContainerSas;
    public int CredentialRevision { get; init; }
    public string TenantId { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string ResourceGroupName { get; init; } = "";
    public string RemoteName => "ctd_" + Id.ToString("N");
    public string RemotePath => RemoteName + ":" + Container + (Prefix.Length == 0 ? "" : "/" + Prefix);
}

public sealed record UploadItem(string Name, long Size, bool Uploading, int Tries);
public sealed record TransferCounters(long Bytes, long CompletedTransfers, long Errors, double BytesPerSecond);
public sealed record MountStatus
{
    public Guid ProfileId { get; init; }
    public MountPhase Phase { get; init; }
    public UploadState Uploads { get; init; } = UploadState.Unknown;
    public string Message { get; init; } = "Not mounted";
    public DateTimeOffset? ObservedAt { get; init; }
    public long? CacheBytes { get; init; }
    public int? Queued { get; init; }
    public int? Uploading { get; init; }
    public bool RecoveryRequired { get; init; }
    public List<UploadItem> Queue { get; init; } = [];
}

public sealed record AppSnapshot
{
    public string Version { get; init; } = CurrentVersion();
    public bool WinFspInstalled { get; init; }
    public bool EngineAvailable { get; init; }
    public int EngineWorkerCount { get; init; }
    public bool WritableEnabled { get; init; }
    public bool Elevated { get; init; }
    public string DataRoot { get; init; } = "";
    public List<Profile> Profiles { get; init; } = [];
    public List<MountStatus> Mounts { get; init; } = [];
    public List<TransferSummary> Transfers { get; init; } = [];

    private static string CurrentVersion()
    {
        var version = typeof(AppSnapshot).Assembly.GetName().Version;
        return version is null ? "unknown-preview" : $"{version.Major}.{version.Minor}.{version.Build}-preview";
    }
}

public sealed record Request
{
    public int ProtocolVersion { get; init; } = Wire.Version;
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public string Operation { get; init; } = "Status";
    public Guid ProfileId { get; init; }
    public Profile? Profile { get; init; }
    public CredentialSubmission? Credential { get; init; }
    public string? StorageAccountName { get; init; }
    public string? ContinuationToken { get; init; }
    public int PageSize { get; init; } = 50;
    public int StatisticsDays { get; init; } = 7;
    public string? DestinationDataRoot { get; init; }
    public bool Confirm { get; init; }
    public bool Force { get; init; }
}

public sealed record CapabilityCheck(string Name, bool Passed, string Detail);

public sealed record Response
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public AppSnapshot? Snapshot { get; init; }
    public List<CapabilityCheck>? Capabilities { get; init; }
    public TransferReport? Statistics { get; init; }
    public string? RelocatedDataRoot { get; init; }
    public string? ExportPath { get; init; }
    public List<string>? Containers { get; init; }
    public string? ContinuationToken { get; init; }
    public static Response Ok(string message = "OK", AppSnapshot? snapshot = null) => new() { Success = true, Message = message, Snapshot = snapshot };
    public static Response Fail(string message) => new() { Message = message };
}

public sealed record MountIntent
{
    public Guid ProfileId { get; init; }
    public string Identity { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public bool WasWritable { get; init; }
    public bool Uncertain { get; init; } = true;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}