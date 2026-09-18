# Local developer preview

Version 0.3.1 is a source-only developer preview, not a certified customer release. New connections begin read-only; writable access requires explicit risk confirmation and should use only disposable or protected data until live write/recovery validation is complete. See the [preview limitations](../README.md#important-limitations).

## Prerequisites for a drive

1. Windows 11 x64.
2. Official WinFsp 2.1.25156 installed using its normal Windows installer workflow. The application does not install or elevate it silently.
3. A normal, **non-administrator** desktop/PowerShell session. Close elevated VS Code and reopen the workspace normally before drive testing.
4. One supported credential: a container-scoped HTTPS SAS with read/list/create/write/delete permissions for writable mode, a storage account key where Shared Key is allowed, or an Azure identity with management Reader, Blob Data Contributor, and user-delegation-key permission. Read-only mode needs only read/list data access.

## Open the locally built application

The package workflow writes the Desktop executable to the repository's artifacts/publish/ContainerToDrive directory and the controller to that payload's controller subdirectory. Keep the entire self-contained folder together; copying only the Desktop executable will not work. An older pre-rename payload is not a ContainerToDrive build.

For development, run [../scripts/Run.ps1](../scripts/Run.ps1) from a normal PowerShell session. It verifies the published payload and uses an isolated development data root under the current workspace. It deliberately refuses an elevated launch. Opening the Desktop executable normally without that wrapper uses the current user's Local Application Data profile.

## Check status and dependencies

The Connections page shows frontend and backend status, engine readiness/running-worker count, and WinFsp installation status. Unknown status means a current controller response is unavailable, not that an existing drive has stopped.

Connection cards flow into two equal-width columns when the window has enough space and one column in narrower windows. Additional connections wrap onto new rows; expanding **Details** keeps the other cards and actions accessible.

Connection cards also show the storage hostname's resolved IP address and **Private endpoint (DNS)** or **Public endpoint (DNS)**. Private classification means RFC 1918 IPv4 or unique-local IPv6 addresses; mixed results, reserved/local addresses, failed lookups, and stale observations are not presented as a confirmed public or private route. **Details** and the row's tooltip show all returned addresses and the observation time.

DNS is resolved from the desktop's Windows session, separately from controller polling, with up to four concurrent lookups and a four-second timeout per lookup. Successful results are cached for 60 seconds by default, configurable in Settings; failures are cached for 15 seconds. Refresh clears the application's cache, but Windows DNS caching still applies. Old results lose their current classification after 75 seconds or when controller contact is unavailable, including between longer configured refresh intervals. The native presentation tests use synthetic addresses and do not perform DNS lookups. Addresses are not added to saved transfer history, session logs, or diagnostic exports.

This is DNS-based inference, not verification of an Azure Private Link resource or the mount worker's actual TCP peer. Hosts-file overrides, VPN changes, proxies, and existing connections can make the active route differ. The lookup does not connect to Blob APIs or read/write storage contents.

Select **Test capabilities** for local component checks and individual results. This verifies engine startup and mount support without mounting a drive or contacting Azure; it is not a cloud permission or write test.

Select **Settings > Components > Install dependencies** when required components are missing. Disconnect all drives first and run the app normally. After confirmation, the app downloads only pinned, hash-verified packages. The rclone fallback is installed for the current Windows user; WinFsp opens its official Windows installer, which may request administrator approval. Downloads can be cancelled from the app; cancel driver installation in its own setup window. A required Windows restart is reported but never initiated automatically. Existing caches are retained. Capability results remain on Connections.

The desktop and controller use wire protocol version 5 for status, capability results, transfer history, and confirmed data-location changes. Update and restart both components together; do not mix binaries from different builds.

## Add and mount the test connection

1. Select **Add connection**, then choose **SAS URL**, **Account key**, or **Sign in to Azure**.
2. For SAS, paste the container URL into the masked field. For account key, enter the lowercase storage account and masked key, load/select a container, then re-enter the cleared key before saving. For Azure sign-in, complete browser authentication and select a subscription, storage account, and blob container. Type part of a name in any dropdown to search. Resource group is an optional filter: without one, storage accounts come from the selected subscription. Container search is disabled until a storage account is selected and is restricted to that account.
3. Confirm the credential-free endpoint/container and choose a free drive letter. **Read-only** is selected by default. Clear it only for a disposable writable target and accept the visible writable-connection warning.
4. Test access and save. A successful listing does not prove write permission, durability, or compatibility with every object.
5. Select **Mount**, then **Open Explorer** after the app reports the drive present.
6. Before disconnecting, close files using the drive. Disconnect through the application; do not terminate unrelated rclone processes or delete caches.

Select **Test** on a connection card to check its saved access without mounting or writing to Azure. The result appears in the status notice and session log. A successful listing is not a write-permission test.

Select **Clone** to create a copy with the same storage target, settings, and saved authentication. It gets a unique copy name and another available drive letter, stays unmounted, and has automatic mounting turned off. The original remains unchanged; its cache and recovery state are not copied. Use **Edit** on the copy to change its name or settings. A clone is another connection to the same Azure data, not a backup; existing writable-overlap protections still apply when mounting.

Azure role assignments can take several minutes to affect Blob data requests. A newly granted Blob Data Reader or Contributor role may still produce `AuthorizationPermissionMismatch`; retry the connection test after Azure propagates the assignment rather than signing in repeatedly.

New connections leave **Automatically mount when the controller starts** off by default. The opt-in setting, profile, selected resource IDs, protected cloud credential, and Azure authentication record are persisted for the current Windows user. Azure dropdowns show progress while searching and have explicit refresh actions. Clear the resource-group filter to search the whole selected subscription. Changing subscription or storage account clears dependent selections. A relaunch silently restores the selected Azure account when its protected token cache remains valid; Conditional Access can still require interactive sign-in.

## Transfer statistics

**Activity & recovery** includes recorded lifetime bytes, period totals, the current measured transfer rate, completed transfers, and engine errors. Filter by connection or view all saved connections. Choose **Today**, **7 days**, **30 days**, **90 days**, or **1 year** for hourly/daily transfer graphs, a traffic-share pie chart, and the expandable **Transfer data** table. Each connection's activity card also shows its lifetime counters.

History begins when this version records an active mount; earlier traffic cannot be reconstructed. The controller samples approximately every 15 seconds, independently of whether the desktop is open, hidden in the tray, or exited with drives running. Mount/disconnect operations also take samples. Slow or unavailable workers and long controller operations can delay sampling. Live rates expire when observations or controller contact become stale.

Counters and worker baselines are stored atomically with DPAPI protection for the current Windows user under the application's `settings` directory. Lifetime totals survive remounts and restarts; hourly detail retains 48 hours and daily detail retains 366 days. Charts use UTC calendar-day windows including today. Missing intervals remain unrecorded rather than being invented as zero. A delta after a sampling gap is assigned to the next successful observation's bucket; an abrupt worker/controller stop can lose its final unsampled traffic. Read/write errors preserve existing history and mark it unavailable. Removed connections are excluded from reports, but their protected history files remain; cloning does not copy history.

The engine's aggregate byte count combines transfers in both directions and can include retries and non-network/server-side work. It is not an upload/download breakdown, Azure billing measure, unique-file count, or proof that writes reached Azure. The pie chart compares connections, not directions. History contains numeric counters, profile/session IDs, and timestamps only, not file names, credentials, or file contents; nothing is sent to an analytics service.

## Choose the application data folder

In **Settings > Storage**, use the folder button beside **Application data location**. Disconnect all drives and resolve recovery first; the button is disabled when the controller is unavailable, any worker is active, or the app is elevated. Choose an empty, separate folder on a fixed local NTFS drive, not a share, linked directory, volume root, or a parent/child of the current location.

After confirmation, the controller copies saved connections, credentials, history, and cache files, verifies copied file hashes, and re-protects path-bound settings for the destination. It checks available disk space and refuses locked caches or another session's active controller. Close other ContainerToDrive windows before continuing. Large caches can take several minutes; the copy has a 30-minute limit. Keep the app running until it finishes.

Only a completed copy saves the new location. The app then reopens there, and auto-mount connections may reconnect. Existing shortcuts, launch arguments, and environment settings pointing to the old root follow a per-user saved location redirect. An enabled Windows startup entry is updated to the new root when possible. Azure browser sign-in may be needed again because its SDK token-cache identity changes with the data path; saved connection credentials are retained.

The original folder is retained as a backup and is never automatically deleted. Failed or interrupted copies do not intentionally switch locations, but may leave a partial destination. If the result is uncertain, reopen the app to check the displayed active path before retrying, and choose an empty folder. Preserve the original until the new location and connections have been verified. A missing redirected destination is reported as unavailable rather than silently creating a fresh empty profile store.

## Windows startup and tray

Enable **Start with Windows** in **Settings > Startup & window** to launch the app in the notification-area tray when you sign in. This is per-user and requires no administrator privileges. For each drive you want reconnected, enable **Automatically mount when the controller starts** in its connection settings. Startup preserves the same saved connections and does not change those per-connection choices. Missing prerequisites, expired credentials, networking, or recovery state can prevent an automatic mount; review the connection status after sign-in.

Closing the main window with **X** hides it to the tray and leaves drives running by default. Clear **Close window to tray** and save preferences to use the existing exit confirmation instead. Double-click the tray icon or choose **Open ContainerToDrive** to return. Use **Exit** for the explicit exit workflow. Disabling **Start with Windows** only removes startup registration; it does not disconnect drives. If Windows Startup Apps settings or organization policy disables the entry, enable it there as permitted. After moving a portable copy, re-enable the option from its new location.

## Settings

The separate **Settings** page groups configuration into these sections:

| Section | Available settings |
| --- | --- |
| Startup & window | Windows sign-in startup, start minimized, close to tray or exit confirmation |
| Storage | Application data-folder selection with verified copy and reopen |
| Display & activity | Host-IP visibility and desktop DNS lookups, DNS refresh (30/60/120/300 seconds), capability collapse (10/30/60 seconds or keep open), default Activity period |
| New connection defaults | Cache target (0.25-1024 GiB) and free-space reserve (1-1024 GiB) |
| Connection settings | Configure each saved connection's source/authentication, name, drive letter, read-only access, automatic mounting, and cache target through the existing dialog; clean, unmounted state required |
| Components | Guarded prerequisite installation |
| Privacy | Local credential/settings protection and cache/diagnostic privacy policy |

Use **Save preferences** for the new window, display, and connection-default preferences. **Reset preferences** resets that draft only; save to apply. Windows startup registration, data-folder selection, and individual connection edits keep their own immediate or confirmed workflows and are not affected by Reset. Unsaved preferences remain in the form when navigating between pages but are lost when the interface exits.

Preferences are stored atomically under the current data root with Windows-user DPAPI protection and restored when the app opens. A missing file uses defaults without creating data; an unreadable or unsupported file is preserved and preference saving is disabled. Elevated sessions cannot change these preferences. New cache defaults do not modify existing connections or clones, enable writes, or turn on automatic mounting. **Start minimized** applies on the next launch; Windows sign-in startup and `--minimized` always start in the tray. Transfer sampling/retention, credential policy, and recovery protections are unchanged.

## Other authentication options

The controller also supports a hidden interactive SAS import mode for a normal terminal. It accepts the credential from keyboard input without echoing or passing it in process arguments. Prefer the desktop wizard for all three methods.

For Entra development, set the non-secret `CONTAINERTODRIVE_AZURE_CLIENT_ID` to an approved public-client application ID before launching. Without it, the Azure SDK developer registration may be used for local evaluation only; configure a project-owned, publisher-reviewed registration before customer release.

## Build and tests

- [../scripts/Bootstrap.ps1](../scripts/Bootstrap.ps1): obtain hash-pinned engine/license material; optional prerequisite download only.
- [../scripts/Build.ps1](../scripts/Build.ps1): build this solution locally.
- [../scripts/Test.ps1](../scripts/Test.ps1): Unit by default; explicitly select LocalIntegration for synthetic Windows/rclone/controller tests. Neither tier mounts or contacts Azure.
- [../scripts/Package.ps1](../scripts/Package.ps1): self-contained unsigned ZIP by default; refuses to overwrite an existing versioned package.
- [../scripts/Verify-Package.ps1](../scripts/Verify-Package.ps1): check the exact published payload/ZIP.
- [../scripts/Clean.ps1](../scripts/Clean.ps1): allowlisted build output only; never development profiles/caches.

Source and build outputs remain in this workspace. Explicit in-app dependency setup stores verified downloads and an optional engine fallback under `%LOCALAPPDATA%\ContainerToDrive\dependencies`. Driver registration, installed SDKs, and protected signing facilities are system concerns rather than source files.

## Security and troubleshooting

- Never share a SAS, account key, access token, or authorization response in screenshots, support exports, issue reports, or shell arguments.
- Missing WinFsp: install the verified official prerequisite; do not disable driver-signature enforcement.
- Administrator-session warning: relaunch normally, not with a registry tweak or elevation workaround.
- Expired credential: renew for the same container while disconnected; cache is retained.
- Unknown/interrupted state: preserve the cache and use the recovery UI. Cached bytes are not proof of Azure persistence.
- Signed installers, live writable certification, complete application compatibility, and production binary releases remain gated.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp repository](https://github.com/winfsp/winfsp).