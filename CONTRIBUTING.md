# Contributing

Issues and pull requests are welcome. Support is best effort, with no guaranteed response time. Original contributions use the [LICENSE](LICENSE); retain third-party attribution and follow [SECURITY.md](SECURITY.md).

For a bug report, include a synthetic reproduction, application/source version, Windows version, expected behavior, and actual behavior. Do not attach real credentials, customer data, private paths, or unredacted logs. Security issues belong in the private reporting channel, not public issues.

Keep pull requests focused. Explain the behavior change, include relevant tests, and update the affected documentation. Discuss substantial changes before implementing them. Do not weaken credential, process-identity, recovery, or filesystem safety checks to make a test pass.

## Prerequisites and local checks

Use a standard-user Windows x64 session, PowerShell 7.4+, and the .NET 10 SDK selected by [global.json](global.json). The SDK is installed separately. Local builds do not require rclone, WinFsp, WiX, Azure credentials, or Node.js.

From the repository, run `./scripts/Build.ps1 -Configuration Release -Locked` and `./scripts/Test.ps1 -Configuration Release -Tier Unit -Locked`. These invoke [scripts/Build.ps1](scripts/Build.ps1) and [scripts/Test.ps1](scripts/Test.ps1); the test script targets [tests/ContainerToDrive.Tests/ContainerToDrive.Tests.csproj](tests/ContainerToDrive.Tests/ContainerToDrive.Tests.csproj) directly. Run `./tests/scripts/WorkspaceSafety.Tests.ps1` for the script parser/path/cleanup guards.

Build supports Debug/Release and `-Locked`; Test supports the same, plus `-Tier Unit`, opt-in `-Tier LocalIntegration`, and `-NoBuild`. NuGet lock files are included for repeatable restores. For an intentional dependency change, regenerate them with the real restore tooling and review the dependency/version/hash changes; do not fabricate lock contents. Runtime-specific publishing must retain the reviewed `win-x64` restore graph.

All entry points resolve the checkout from their location, even when invoked from another current directory. [scripts/Common.ps1](scripts/Common.ps1) scopes NuGet, CLI-home and temporary paths, restores the calling process environment, rejects output reparse ancestry, and serializes cooperating wrappers. It does not relocate OS-managed caches or protect against concurrent hostile filesystem mutation.

## Dependencies and development launch

Review [scripts/dependencies.json](scripts/dependencies.json), which contains pinned upstream versions and SHA-256 values. Changing a pin requires source/provenance review; do not bless a download merely by hashing what was just received. The optional WinFsp MSI also requires its expected upstream Authenticode signature.

After review, `./scripts/Bootstrap.ps1` fetches only the pinned engine/checksum list and license material. `./scripts/Bootstrap.ps1 -DownloadWinFsp` additionally downloads the official prerequisite, **without installing it**. See [installer/INSTALLATION.md](installer/INSTALLATION.md).

`./scripts/Run.ps1` starts only the verified published desktop in a non-elevated session with an isolated development data root. Its manifest-checked data-root contract must continue to match the desktop and controller. It does not accept secrets on its command line or stop existing mounts.

## Packaging

After trusted dependencies are available and all application processes have been closed through the normal disconnect/exit workflow, use `./scripts/Package.ps1 -Configuration Release -Version 0.3.1 -Locked`. [scripts/Package.ps1](scripts/Package.ps1) publishes Desktop and Controller separately, keeps their self-contained win-x64 runtimes isolated, and creates a verified **UNSIGNED developer ZIP**. Trimming, AOT, and single-file publishing are disabled. Existing versioned packages are not overwritten.

`./scripts/Verify-Package.ps1` rechecks the current payload. Optional `-ZipRelativePath` verifies a workspace-relative ZIP against every staged file. Optional `-RequireSigned` rejects unsigned app binaries. Keep the staged payload and trusted dependency archive while performing these local checks. Hash manifests detect changes relative to their trust anchor; an unsigned manifest beside a ZIP is not independently authenticated provenance.

Optional `-Msi` requires the separately prepared, reviewed WiX 6.0.2 tool at the exact location recorded in the dependency manifest. No automatic tool installation occurs. Review [installer/README.md](installer/README.md) first; MSI building is not lifecycle certification.

Signing is opt-in with `-Sign`, `-CertificateThumbprint`, `-SignToolPath`, `-TimestampUrl`, and `-SourceRevision`. Use an existing CurrentUser/My code-signing identity with its private key managed outside the workspace and an absolute trusted Windows SDK SignTool executable. The timestamp URL must be HTTPS without embedded credentials/query. Missing identity, timestamp/signature verification failure, or signing errors fail the build. Only project-owned executables/assemblies and the optional MSI are signed; upstream binaries are not altered. No signing credentials belong in pull-request CI.

The inventory is explicitly **not** a standards-compliant SBOM and retains open license/installer review items. Signing does not waive these gates or confer permission to publish.

## Cleanup and changes

Use `./scripts/Clean.ps1 -WhatIf` to preview, then `./scripts/Clean.ps1` when no ContainerToDrive instance or external build is using its output. [scripts/Clean.ps1](scripts/Clean.ps1) removes only its exact artifact allowlist. It never purges development profiles/caches, tooling, NuGet caches, arbitrary temporary directories, or unclassified evidence. It never stops a process.

Add deterministic synthetic tests with `Category=Unit`; do not introduce HTTP clients, cloud SDK calls, driver/mount operations, or credential discovery into this tier. Run local integration and interactive desktop checks only in their documented environments. Real Azure, driver, and mount testing requires separate explicit environment/ownership approval. See [tests/README.md](tests/README.md) for test boundaries and remaining release gates.

Before submitting changes, inspect actual payloads and review licensing/security implications. Do not claim tests were run when only source or editor diagnostics were inspected.

The publication allowlist excludes local application state, generated artifacts, historical plans, and obsolete project copies. Do not force-add ignored files. Review the complete diff and any scan findings before creating a commit; neither a passing scanner nor a hash manifest proves that material is safe to publish. Publishing packages, creating releases, installing drivers, and provisioning cloud resources are separate actions, not part of the build/test workflow.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).