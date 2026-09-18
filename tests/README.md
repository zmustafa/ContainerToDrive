# Test tiers and evidence

## Unit tests

[ContainerToDrive.Tests/ContainerToDrive.Tests.csproj](ContainerToDrive.Tests/ContainerToDrive.Tests.csproj) uses pinned xUnit and Microsoft.NET.Test.Sdk packages. Coverage includes credential/source validation, wire and redaction behavior, form rules, Azure search state, compiled WPF dialogs, endpoint classification, preferences, startup settings, and transfer models. JSON-secret and multi-part bearer redaction cases are regression gates, not permission to log credentials.

From a standard-user Windows checkout with the required SDK and PowerShell, run:

```powershell
./scripts/Build.ps1 -Configuration Release -Locked
./scripts/Test.ps1 -Configuration Release -Tier Unit -Locked
./tests/scripts/WorkspaceSafety.Tests.ps1
```

[../scripts/Test.ps1](../scripts/Test.ps1) selects the exact project and `Category=Unit`, writes a fresh local TRX, and fails for zero executed cases, failures, or skipped tests. Restore can contact NuGet; unit tests do not contact Azure, mount drives, install drivers, or discover credentials. Trait/environment guards route tests; they are not a network sandbox for untrusted code.

Account names, keys, signatures, profiles, and content fixtures are synthetic. Public-cloud-shaped URLs are parser input only: never resolve, request, or mount them. Do not supply customer credentials through source, environment, command-line arguments, or CI.

## Workspace safety

[scripts/WorkspaceSafety.Tests.ps1](scripts/WorkspaceSafety.Tests.ps1) is a dependency-free PowerShell guard suite. It parses the authored scripts and exercises workspace traversal rejection, cleanup allowlisting, synthetic recovery/tool retention, and junction rejection in a disposable workspace-local fixture. It performs no builds, installs, cloud operations, or application launches.

## Opt-in local checks

- [ContainerToDrive.IntegrationTests/README.md](ContainerToDrive.IntegrationTests/README.md) documents synthetic Windows persistence, DPAPI, named-pipe, controller, and local rclone tests. Invoke them explicitly with `./scripts/Test.ps1 -Configuration Release -Tier LocalIntegration -Locked` after obtaining the documented prerequisites. They do not mount drives or access Azure.
- [desktop/README.md](desktop/README.md) describes interactive WPF presentation checks using synthetic data, including responsive cards and compiled dialogs. These require an interactive Windows desktop and are not part of hosted unit CI.

Local reports and captures are generated evidence, not source files. Review and redact them before sharing; do not attach an entire diagnostics, application-data, or test-output directory to a public issue.

## Remaining release gates

Passing build and unit checks does not establish production readiness. Real mounted read-only enforcement, write persistence, open writers, delayed uploads, disconnect races, crash recovery, expired credentials, low disk, and independent remote-byte verification require dedicated disposable resources and explicit environment/ownership approval.

Clean-VM installer install/upgrade/uninstall, active-mount blocking, accessibility, multi-user isolation, signing, and complete third-party redistribution review remain separate gates. Record checks as passed, failed, or not run; do not replace missing coverage with always-passing stubs or infer Azure durability from queue or serialization tests.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).