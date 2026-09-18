# Developer installation and prerequisites

**Pre-release developer package, not a certified customer release.** An UNSIGNED label means ContainerToDrive application binaries have not been Authenticode-signed. An AUTHENTICODE-SIGNED-APP label covers the explicitly verified project-owned binaries, not every third-party file. Neither label implies SmartScreen reputation or completion of reliability/license review.

## WinFsp is a separate shared prerequisite

1. Use a disposable test machine/snapshot for initial installation testing. Review the package hashes/signatures against a trusted source before opening any executable or installer.
2. Check existing WinFsp use by other applications before making changes. The candidate version is the official v2.1 asset named winfsp-2.1.25156.msi. Do not downgrade or remove an existing shared installation to satisfy this application.
3. Obtain the official **unmodified** MSI from the [v2.1 release](https://github.com/winfsp/winfsp/releases/tag/v2.1), or the separately verified optional Bootstrap download. Validate both its reviewed SHA-256 and upstream Authenticode signature. Do not run a downloaded MSI if either check fails.
4. An administrator must explicitly launch the prerequisite installer, review its license/options, approve UAC if appropriate, and handle any reboot requirement. A cancelled or failed prerequisite installation is not success. Do not bypass driver-signature enforcement.
5. If a compatible shared WinFsp is already present, preserve it. Missing/unhealthy driver or runtime files require a separately approved official repair coordinated with other applications. ContainerToDrive does not own the shared driver and never removes it on uninstall.

The developer MSI checks only for a WinFsp installation marker, **not** driver health or compatibility. Actual version, driver/runtime availability, and reboot handling need the application's validated prerequisite checks and clean-machine evidence.

## Application

The ZIP contains self-contained win-x64 application files. Extract the **whole** archive into an application-owned folder; do not mix files from different versions. The optional MSI installs under protected 64-bit Program Files and adds standard desktop/Start-menu shortcuts. It installs no background service and does not automatically launch the desktop after elevation. Start it yourself from an ordinary, non-elevated Windows session; elevated mounts may not appear in ordinary Explorer.

A production deployment must use the approved signed artifacts and validated Windows support matrix, neither of which is asserted by this scaffold. The package does not grant write-mode or data-durability assurances.

## Upgrade, repair, and uninstall

Close applications using every drive, follow the tested disconnect workflow, resolve pending/unknown upload or recovery state, and stop ContainerToDrive before changing binaries. **Never force-kill a worker just to complete installation.** Do not upgrade a dirty/unknown cache across engine versions without a validated recovery path.

The first ContainerToDrive run can migrate a stopped legacy BlobToDrive data root. It moves the default directory without copying cache contents, upgrades profile records atomically, re-encrypts credentials and protected settings for the current Windows user, and retains migration backups. It refuses to start a new controller while the legacy controller lock is held. The Azure token-cache name changes, so interactive Azure sign-in can be required again even when profile credentials remain recoverable.

The MSI does not request Restart Manager shutdown/restart, purge user profiles/cache, or uninstall WinFsp. However, this authoring does **not yet implement a proven active-controller/mount upgrade/uninstall blocker**. Use it only in a controlled VM with no active mounts until that lifecycle gate is implemented and tested. Files-in-use dialogs and reboot scheduling are not proof of safe upload completion.

User data is retained independently of application uninstall. Preserve uncertain recovery data; deleting local files does not revoke remote credentials. See [SECURITY.md](../SECURITY.md) and [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) in the source checkout; the package includes copies of both at its top level.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).