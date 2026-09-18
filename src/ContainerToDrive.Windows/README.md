# Windows integration API

Namespace: `ContainerToDrive.Windows`. Target: `net10.0-windows`, nullable and implicit
usings enabled. References Core and `System.Security.Cryptography.ProtectedData`
version `10.0.0`; filesystem/pipe ACL APIs come from .NET.

## Public contract

All classes below are public. `AppPaths`, `PipeWire`, and `LocalPipe` are static;
`ProfileStore`, `ControllerClient`, and `WorkerJob` are sealed.

### AppPaths

- `string ResolveDataRoot(string[] args)`
- `bool IsElevated { get; }`
- `string PipeName(string dataRoot)`
- `string SessionKey(string dataRoot)`
- `string CurrentSid { get; }`
- `void SecureDirectory(string path)`
- `bool WinFspInstalled { get; }`

Data-root precedence is `--data-root directory` (also accepts `--data-root=directory`),
then `CONTAINERTODRIVE_DATA_ROOT`, then the legacy `BLOBTODRIVE_DATA_ROOT`, then
LocalApplicationData/ContainerToDrive. The default location can migrate a legacy
BlobToDrive directory, and saved per-user data-location redirects are resolved.
Relative roots are resolved once against the working directory; clients launch
controllers with an absolute root. Duplicate/missing options, volume roots, UNC/device paths, and
alternate data streams are rejected. Directory protection requires fixed local
NTFS and rejects existing reparse points throughout the path. Owner is the current
SID; only that SID and SYSTEM receive full control, inherited by new children.
Existing unrelated ancestor ACLs are not rewritten. This is not a recursive repair
of arbitrary pre-existing cache trees or a sandbox against the same user/admin.

`SessionKey` is a SHA-256 identifier of normalized root, current SID, and Windows
session ID. `PipeName` prefixes it with `ContainerToDrive-`. WinFsp detection checks
machine installation registry views and the process-architecture DLL; it does not
certify driver loading, compatibility, or successful mounting.

### ProfileStore

- `ProfileStore(string dataRoot)`
- `string DataRoot { get; }`
- `List<Profile> List()`
- `Profile? Find(Guid id)`
- `Profile Save(Profile profile, CredentialSubmission? credential)`
- `CredentialSubmission ReadCredential(Guid id)`
- `string ReadSecret(Guid id)`
- `Profile Renew(Guid id, CredentialSubmission credential)`
- `void Renew(Guid id, string sas)`
- `void Remove(Guid id)`
- `void WriteIntent(MountIntent intent)`
- `MountIntent? ReadIntent(Guid id)`
- `string CachePath(Guid id)`
- `string RuntimePath(Guid id)`

The constructor protects the data root and profile, intent, cache, and runtime
directories. Each store operation takes a short-lived, non-deleted `FileShare.None`
lock, with a five-second contention bound, not a controller-lifetime lock. Private
helpers avoid nested lock acquisition. Cache/runtime path helpers create protected
GUID-named directories; they do not acquire cache-worker ownership.

The store validates typed credentials for container SAS, account key, and Microsoft
Entra profiles. Renewal requires the saved authentication kind and expected
credential revision, increments the credential revision, derives expiry from the
validated credential, and preserves the storage target. The controller owns
mount/recovery guards and source-identity changes.

Schema-versioned records atomically commit metadata plus DPAPI CurrentUser
ciphertext in one replacement, with flushed same-directory temporary files and a
previous-record backup. SAS bytes are not normalized or persisted in plaintext.
Missing/duplicate/invalid record fields and unsupported schemas fail closed;
backups are never silently promoted. No raw store file belongs in diagnostic
exports. Export public `Profile` objects instead.

Removal writes a tombstone to prevent stale recreation; it does **not** purge
cache, runtime, intent, or protected backup material. The controller must gate
removal, migration, and credential changes on mount/recovery state and perform
deliberate retention/cleanup. The store does not certify multi-session operation.
`ReadSecret` can return an expired credential for controller handling. Managed
strings cannot promise perfect zeroization; owned plaintext byte buffers are wiped.

### PipeWire

- `Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)`
- `Task<T> ReadAsync<T>(Stream stream, CancellationToken token = default)`

Framing is a four-byte little-endian positive byte count followed by UTF-8 JSON
using Core `Wire.Json`. Maximum payload is `Wire.MaxMessageBytes` (1 MiB), excluding
the header. Truncation, invalid lengths/UTF-8/JSON, and JSON null are errors.
Output is size-bounded before sending the header. Serialize access per stream
direction and discard the connection after cancellation or a framing error.

### ControllerClient

- `ControllerClient(string dataRoot)`
- `Task EnsureStartedAsync(CancellationToken token = default)`
- `Task<Response> SendAsync(Request request, CancellationToken token = default)`

The client connects to `.` with asynchronous, `CurrentUserOnly` pipes and
identification-level impersonation. Every connection verifies the server PID,
process-token user/owner SID, Windows session, elevation, and exact executable
path **before** any request is sent. Readiness is an authenticated Status exchange,
not simply a successful pipe connection. Unexpected ownership fails closed.

Discovery checks the application's `controller` subdirectory, then the application
directory, requiring the executable and paired managed assembly for either packaged
location. It then checks the checkout's controller debug/release artifact
directories beneath an ancestor with the workspace marker. It never searches PATH
or trusts a basename alone. The
installation/checkout and its dependencies must themselves be trusted; this is
not binary signature/hash verification or protection against same-user injection.

Startup is bounded to twelve seconds and coordinates concurrent clients with a
session-specific startup file lock. Elevated launch is refused. The controller
must still enforce its own lifetime singleton/cache ownership. Sends have a
five-second connection bound and ninety-second overall bound, or earlier caller
cancellation. Mutations are never automatically replayed on connection failure.

### LocalPipe

- `NamedPipeServerStream CreateServer(string root)`
- `void ValidateClient(NamedPipeServerStream pipe)`

Call `ValidateClient` after **every** accept, before reading a request; dispose a
rejected connection. Creation uses an asynchronous byte-mode pipe, NETWORK deny,
and current SID/SYSTEM ACL entries atomically. The server deliberately does not
pass the `CurrentUserOnly` flag: .NET ignores custom ACLs when it is present.
Explicit token validation enforces its user/owner/elevation semantics plus session
isolation. Therefore an elevated desktop cannot inspect an unelevated controller
through this channel. Accept loops, operation allowlists, protocol validation,
timeouts, rate limiting, and application-level concurrency belong to the controller.

### WorkerJob

- `WorkerJob()`
- `void Assign(Process process)`
- `void Dispose()` (`IDisposable`)

An unnamed, non-inheritable native job uses kill-on-close. Assignment verifies the
explicit process handle's user/session/elevation, direct parent, and creation time;
it never looks up or kills workers by name. Keep the job alive for the worker
lifetime. Assign immediately after spawn, before secrets/mounting. A failure must
abort provisioning; the launcher still owns cleanup of that exact unassigned child.
Ordinary `Process.Start` followed by assignment has a pre-assignment crash window;
eliminating it needs suspended/native process creation in the launcher. Job close
is an interruption, not a graceful disconnect; persist intent first and retain
cache for recovery. Nested-job restrictions fail rather than weaken containment.

## Validation

See [the test tiers](../../tests/README.md) and
[local Windows integration checks](../../tests/ContainerToDrive.IntegrationTests/README.md).
A successful compile or editor check does not substitute for DPAPI, ACL, pipe,
process-identity, job, persistence, or live mounted-filesystem validation.