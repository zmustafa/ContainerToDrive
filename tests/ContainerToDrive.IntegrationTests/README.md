# Local Windows/rclone integration tests

## Scope and routing

[ContainerToDrive.IntegrationTests.csproj](ContainerToDrive.IntegrationTests.csproj) targets `net10.0-windows`, references Core/Windows/Rclone, and uses Microsoft.NET.Test.Sdk **17.14.1**, xUnit **2.9.3**, and Visual Studio runner **3.1.1**. Every test class has `Trait("Category", "LocalIntegration")`. No skips, prerequisite-as-success branches, real cloud credentials, downloads, or automatic installation are present.

Solution registration and a separate entry point selecting this exact project and `Category=LocalIntegration` are intentionally left to the caller. The existing unit entry point is unchanged. The local-integration runner should reject zero executed tests, failures, and skips, and keep results workspace-local. Run from the checkout's artifacts tree, not a copied/shadow-copied directory outside the checkout.

The recorded Release run executed and passed **88 cases with zero failures/skips**. Fresh runs remain required after every relevant source/dependency change; editor diagnostics are not test evidence.

## Prerequisites and containment

- Windows with .NET 10, a current interactive user's DPAPI profile, and a checkout on a fixed local NTFS volume with no reparse-point ancestors. Missing/unusable prerequisites fail tests.
- The repository's already-bootstrapped rclone **1.75.1** executable and archive, verified by the unchanged `EngineLocator.FindVerified`. No PATH/global executable fallback or download is used.
- These are **non-mount** tests. WinFsp, free drive letters, admin rights, Developer Mode, Azure access, storage accounts, and emulators are not required. An already-elevated host is allowed: `StartAsync` uses the real `WorkerJob` ownership checks, but `MountAsync` is never called. There is no elevation/de-elevation workaround.
- [LocalTestRoot.cs](LocalTestRoot.cs) discovers the workspace strictly from `AppContext.BaseDirectory` ancestors and repository markers. Each disk fixture creates a new GUID root beneath the checkout's `.local/test-runs` directory. There is no CWD/environment/home/temp/application-data fallback. Paths and ancestors are independently checked for containment and reparse points before fixture writes.
- Generated roots are retained for inspection, including corrupt records and synthetic cache evidence. They are not recursively deleted. The junction test removes only its own link, never its target. It creates an ordinary directory junction through `FSCTL_SET_REPARSE_POINT`, not a volume mount or privileged symbolic link. No firewall, registry, account, driver, or volume-ACL changes occur.
- Only fabricated future-dated container SAS/user-delegation strings and random synthetic 64-byte account keys are generated. Signatures are deliberately invalid. Never replace them with working credentials in this tier. No request is made to fabricated Azure URLs.

## Inventory

Coverage includes ProfileStore schema-v1 migration/schema-v2 DPAPI records and credential revisions; strict pipe framing/JSON/credential unions; AppPaths ACL/reparse/WinFsp detection; real rclone TLS/RC/process ownership and three-kind parameter allowlists; and actual controller IPC for SAS, account-key, and Entra credential save/renew/remove without cloud access.

Supporting helpers: [SyntheticCredential.cs](SyntheticCredential.cs), [LocalTestRoot.cs](LocalTestRoot.cs), [LocalJunction.cs](LocalJunction.cs), and [OwnedProcessCommandLine.cs](OwnedProcessCommandLine.cs).

Every worker case has bounded startup/RC operations and disposal. The test opens and retains the exact process handle returned by authenticated `core/pid`, checks executable/session/creation time, and asserts that same process has exited when disposal returns. It also checks certificate/key deletion, owner-lock release, cache sentinel preservation, and all remaining fixture files for synthetic secrets. It neither enumerates nor kills processes by name; only production worker disposal terminates its owned child/job. A disposal timeout fails, never counts as successful cleanup.

## RC and secret boundary

`StartAsync` makes its normal `rc/noopauth`, `core/version`, and `config/create` calls with a fabricated SAS. Separate pure tests assert mutually exclusive SAS/account-key/Entra parameter dictionaries and `env_auth=false`/`use_az=false`; they do not contact Azure. Additional real-worker calls are restricted to `core/version`, `core/pid`, `config/paths`, `config/listremotes`, and `rc/noopauth`. Production disposal uses `core/quit`.

The version-pinned [rclone configuration implementation](https://github.com/rclone/rclone/blob/v1.75.1/fs/config/config.go) normalizes `NUL` to an **empty string** for memory-only configuration. Its [RC implementation](https://github.com/rclone/rclone/blob/v1.75.1/fs/config/rc.go) returns that exact string in `config/paths` and remote names without a trailing colon in `config/listremotes`. The tests assert those values, not permissive alternatives.

Authentication tests use reflection **only to read the private HTTP client's BaseAddress**. They do not read/replace authorization headers, passwords, production TLS callbacks, or production classes. A separate unauthenticated `HttpClientHandler` bypasses certificate trust **only for the exact HTTPS IPv4-loopback endpoint** so HTTP 401 can be observed. This is a **test-local certificate bypass, not production certificate validation and not a TLS-spoof test**. Proxies, redirects, cookies and default credentials are disabled, and no SAS or genuine RC credential is sent by that handler. TLS/connectivity errors fail rather than masquerading as authorization rejection.

The command-line check uses `NtQueryInformationProcess` only on the held worker handle; unsupported/access-denied queries fail. It never invokes a shell or queries other processes. Credential comparisons, captured callbacks and disk scans use generic failure messages rather than printing SAS values, file contents, or command lines. The exclusively held, zero-length cache owner lock is checked for length while active and actually opened/scanned after disposal; other unreadable files are failures, not exclusions.

Scans cover ordinary files in the generated root at the assertion points (including backups), not the entire disk, transient deleted files, alternate streams, OS paging/DPAPI internals, or memory dumps. No ambient global application data is searched. The endpoint allowlist and trait are test design constraints, not a network sandbox or proof against arbitrary future production changes.

## Source finding: trailing-dot/space rejection follows normalization

Static source inspection plus [documented Windows path normalization](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats#trim-characters) identifies a policy gap in [AppPaths.NormalizeDataRoot](../../src/ContainerToDrive.Windows/AppPaths.cs#L131-L134): `Path.GetFullPath(path)` at line 131 strips terminal dots/spaces **before** the rejection predicate at line 134 sees them. Consequently ordinary input ending in `trailing.` or `trailing ` can be accepted as the normalized `trailing` directory instead of rejected.

The two suffix rows in [AppPathsTests.cs](AppPathsTests.cs#L42-L57) remain explicit, non-skipped regression gates and are **expected to fail against the current implementation**. This is a static expectation, not a claimed runtime reproduction.

**Proposed production fix (not applied):** inspect the original path's components, treating both slash styles as separators, before `Path.GetFullPath`; reject non-dot-segment components ending in a period or space. Preserve intentional `.`/`..` canonicalization and trailing separators, and retain all existing normalized volume/UNC/device/invalid-character checks. Do not fix the tests by trimming the hostile input or accepting normalization as rejection.

Production source, solution, entry points, and other test directories were not modified.