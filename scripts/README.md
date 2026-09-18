# Local entry points

These entry points build, test, and prepare the local developer preview. See [../README.md](../README.md) for the source-only release scope and remaining production limitations.

All scripts require Windows and PowerShell 7.4+. They resolve the repository from their own location. No wrapper installs an SDK/driver, creates Azure resources, publishes artifacts, or modifies user-wide environment settings.

| Entry point | Contract |
| --- | --- |
| [Bootstrap.ps1](Bootstrap.ps1) | Reviewed rclone 1.75.1 archive hash plus published SHA256SUMS match; full pinned WinFsp license; optional verified WinFsp MSI download only |
| [Build.ps1](Build.ps1) | Explicit solution restore/build, Debug/Release, optional locked restore, checked native exit codes |
| [Test.ps1](Test.ps1) | Unit default or opt-in LocalIntegration, fresh TRX, rejects failed/skipped/zero-test runs; no Azure or drive mounts |
| [Package.ps1](Package.ps1) | Separate self-contained win-x64 Desktop/Controller runtimes, notices, inventory, hashes, verified ZIP, optional WiX MSI/signing |
| [Verify-Package.ps1](Verify-Package.ps1) | Complete payload file-set/hash comparison, original engine trust, required notices/signatures, optional ZIP byte verification |
| [Run.ps1](Run.ps1) | Standard-user verified desktop launch with a manifest-checked, isolated development data root |
| [Clean.ps1](Clean.ps1) | ShouldProcess/WhatIf, exact artifact-directory allowlist, reparse preflight, no application-data/tool/cache purge |

[Common.ps1](Common.ps1), [Dependencies.ps1](Dependencies.ps1), and [PackageSupport.ps1](PackageSupport.ps1) are dot-sourced helpers, not standalone commands. [../tests/scripts/WorkspaceSafety.Tests.ps1](../tests/scripts/WorkspaceSafety.Tests.ps1) exercises parser/path/hash/cleanup guards without third-party test tooling.

## Dependency and packaging contracts

- The [unit test project](../tests/ContainerToDrive.Tests/ContainerToDrive.Tests.csproj) and local integration project are included in the solution. The test script selects the requested tier directly. Keep the included lock files synchronized with intentional dependency changes and use `-Locked` for repeatable builds.
- [dependencies.json](dependencies.json) now contains reviewed upstream SHA-256 pins: rclone ZIP, full WinFsp license, and optional WinFsp MSI. The MSI signature was also validated. Do not silently replace pins with latest versions.
- The generated runtime engine manifest has `schemaVersion = 1`; its `rclone` object has `version`, `path`, and `sha256`. `path` is `tools/rclone.exe`, relative to the package root. The controller has its own runtime subdirectory and resolves this root explicitly. The `winfsp` object has `version` and `installation = external-shared-prerequisite`. Source reader, packaging, and verification are aligned.
- Bootstrap stores the verified engine in the repository-local versioned tools directory. Packaging re-derives its executable hash from the trusted ZIP; it does not trust the mutable cached verification report alone.
- Keep `development.dataRootEnvironmentVariable` and `development.dataRootContractVerified` aligned with the actual Desktop/Controller data-root contract. Launch arguments must not contain credentials.
- Redaction tests include quoted JSON secrets and multi-part bearer values; retain these regression cases when changing logging or diagnostic output.
- WiX 6.0.2 is a candidate. Prepare the exact local tool separately after terms/provenance review, then validate the build/ICE output. Runtime-pack metadata and full license-file locations also need validation against the selected .NET SDK; missing material blocks packaging.
- MSI registry detection is presence-only. Active-controller/mount lifecycle blocking, driver/version/reboot checks, source UI attribution, complete Go/NuGet licensing, and clean-VM evidence remain release gates.

For a local 0.3.1 package, run `./scripts/Package.ps1 -Configuration Release -Version 0.3.1 -Locked` after bootstrap. Close the application and controller through their normal workflow first. Packaging refuses active processes and existing versioned outputs; do not bypass those guards or remove an active recovery cache. A successful package check establishes payload consistency, not signing, installer lifecycle, cloud durability, or licensing certification.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).