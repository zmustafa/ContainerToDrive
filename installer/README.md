# Installer authoring

[ContainerToDrive.wxs](ContainerToDrive.wxs) uses WiX's v4 XML namespace and the WiX 6 Files harvesting element. The 6.0.2 tool is a **candidate** pending availability, terms, compiler/ICE, and clean-VM validation. It is not installed automatically.

[../scripts/Package.ps1](../scripts/Package.ps1) with `-Msi` supplies the publish directory, three-part MSI product version, explicit signing-status label, win-x64 architecture, and workspace-local intermediate/output paths. It builds only; it never launches Windows Installer or uploads results. Full self-contained publish output is harvested; the desktop executable is authored explicitly to own its advertised shortcuts.

Properties of this initial authoring:

- Per-machine, protected Program Files default; no writable runtime data in the install directory.
- Fixed upgrade identity, downgrade rejection, transactional major-upgrade removal schedule.
- Registry install/version/status markers and desktop/Start-menu shortcuts.
- No elevated post-install launch, service, autorun registration, process termination, custom driver ownership, shared-driver removal, or user-data deletion.
- WinFsp presence-only registry lookup; prerequisite distribution/installation is separate and manual.
- Restart Manager shutdown/restart is disabled. This is **not** a substitute for a tested active-mount lifecycle blocker.

Before customer delivery: validate actual WinFsp version/health detection, active-controller and dirty-cache install/upgrade/uninstall blocking, rollback, repair, UAC cancellation, reboot-required handling, coexistence, and standard-user shortcuts on clean VM snapshots. Do not certify these from a successful MSI compilation. Record explicit passed, failed, or not-run results for each lifecycle check; see [../tests/README.md](../tests/README.md) for the other release gates.

The user-facing [INSTALLATION.md](INSTALLATION.md) is copied into developer packages. Missing full upstream notices block package assembly. rclone's source-captured [licenses/rclone-COPYING.txt](licenses/rclone-COPYING.txt) is from the [pinned upstream COPYING](https://raw.githubusercontent.com/rclone/rclone/v1.75.1/COPYING); Bootstrap obtains and verifies the full WinFsp v2.1 license including the GPLv3 text and FLOSS exception.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).