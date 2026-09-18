# Native desktop checks

## Connection dialog

Build the debug desktop project, then run [ConnectionDialog.Tests.ps1](ConnectionDialog.Tests.ps1) in a fresh interactive PowerShell 7.4+ terminal on Windows:

```powershell
dotnet build .\src\ContainerToDrive.Desktop\ContainerToDrive.Desktop.csproj
& .\tests\desktop\ConnectionDialog.Tests.ps1
```

This check loads the current dialog XAML and searchable control with synthetic resource lists and detached window event handlers. It checks in-dropdown loading, typed substring filtering, WPF Down/Enter selection, disabled/reset container state, and layout at 800 x 660 and 520 x 460. PNG captures are written under `artifacts/evidence/ux-implementation`. It does not sign in, access Azure, start the backend, read saved profiles, or mount drives; it is not a live discovery test. The test briefly focuses its own windows, so avoid interacting with them while it runs.

WPF caches loaded assemblies for the lifetime of the host. After rebuilding, use a fresh terminal; the script rejects a stale assembly instead of testing old code. Scope validation, debounce, caching, and stale-response rejection are also covered by [AzureSearchTests.cs](../ContainerToDrive.Tests/AzureSearchTests.cs).

[ConnectionWindowTests.cs](../ContainerToDrive.Tests/ConnectionWindowTests.cs) also constructs the compiled Add and Edit dialogs on an STA thread with real XAML event handlers. It covers SAS, account key, and Entra profiles, retained edit fields, and unaccepted writable consent using synthetic data and no controller or Azure calls. This catches runtime event-delegate binding errors that the detached-handler layout check cannot detect.

## Connection card

Build the debug desktop project, then run `& .\tests\desktop\ConnectionCard.Tests.ps1` from the repository root in a fresh interactive PowerShell 7.4+ terminal on Windows. This loads the real card template with synthetic data and detached click handlers, checks all eight actions on one row, compact height, long-name/address stability, the host-IP/endpoint-type row, expandable DNS metadata, caveat tooltips, and disabled bindings. Normal/minimum-width captures are saved under `artifacts/evidence/ux-implementation`. IP addresses in those captures are synthetic; no DNS lookups are started by the native harness. Offline resolver tests cover IPv4/IPv6 private/public/reserved/mixed classification, cache expiry, refresh during in-flight success/failure, timeout, cancellation, target validation, and stale/wrong-host card results.

The compiled main-window check also lays out five synthetic connections while resizing through wide, breakpoint-adjacent, normal, and minimum window widths. It verifies two equal-width columns on wide windows, one column on narrow windows, the odd final card, row spacing, all action bounds, and expanding Details without overlapping or stretching other rows. Wide/minimum captures are saved as `connection-grid-wide.png` and `connection-grid-minimum.png` in the same evidence directory.

The same script loads the pinned OxyPlot assemblies and renders the real transfer dashboard with synthetic history at normal/minimum widths. It checks plot areas, filter bounds and bindings, missing intervals, and empty-state visibility, and captures `transfer-statistics-normal.png` and `transfer-statistics-minimum.png`. These screenshots are synthetic presentation evidence, not live Azure transfer measurements. Ledger/model tests cover resets, remounts, UTC boundaries, retention, serialization, gaps, filtering, and stale rates. Offline integration tests exercise protected history, corrupt-file preservation, populated controller reports, and an actual 8 KiB temporary local-to-local engine copy; no cloud transfer is performed.

The same check uses a test subclass that suppresses application startup, constructs the compiled main window without starting polling, and verifies the Windows startup checkbox binding, actual close-to-tray behavior, and tray reopening. It briefly creates and disposes its own tray icon. It then feeds synthetic capability responses and exercises the real ten-second auto-collapse timer, retained results, manual reopening, repeated tests, failures, and timer cleanup on close. It does not load real profiles, start a controller, change Windows startup registration, or contact Azure. Startup command and registration tests use a temporary non-startup registry key; an actual Windows sign-in cycle remains a manual check. Clone persistence and authentication behavior are covered separately by the offline controller integration tests.

The Settings checks navigate the compiled main window, verify relocated controls and mounted-drive guards, measure section/control bounds at 940 x 680 and 760 x 520, and capture each scroll viewport as `settings-normal-*.png` / `settings-minimum-*.png`. They exercise invalid drafts, save/reopen, DNS visibility, Activity defaults, keep-open results, and close-to-exit busy guards. Default reads must not create data; saving creates only a temporary protected preference record that is removed on completion. Existing connection defaults and real Windows startup registration remain unchanged. Unit tests additionally cover preference persistence, unsupported-record preservation, safe new-profile defaults, and configurable DNS cache lifetimes. Real exit confirmation with active drives, login startup, multi-instance changes, Narrator, and high-contrast certification remain manual checks.

## Elevated desktop presentation smoke

The native main-window check also verifies the data-location browse action's accessible name, tooltip, and unavailable/mounted/recovery/idle enablement. It does not open the system picker or relocate real data. Isolated unit/integration checks cover path separation, saved redirect chains, missing destinations, copy verification, protected-setting re-encryption, retained profile credentials/history/cache bytes, unconfirmed/recovery refusal, and locked-cache failure. Real folder selection, a large cross-volume copy, and application relaunch remain manual checks.

[scripts/Smoke-Desktop.ps1](../../scripts/Smoke-Desktop.ps1) is a separate, opt-in
smoke for the **already published self-contained preview**. It does not build,
restore, package, install, start the controller, invoke rclone, or perform Azure
operations. Merely adding this script is **not execution evidence**.

## Run deliberately

- Use an **already elevated**, interactive PowerShell **7.4 or newer** session
  on an unlocked Windows desktop. This means `pwsh`, not Windows PowerShell 5.1.
  The script refuses a non-elevated session and never requests elevation.
- The checkout must be on a fixed local NTFS volume. The existing self-contained
  publish output must be present under the repository's artifacts/publish/ContainerToDrive
  directory. There is no fallback to debug builds, PATH, or an installed copy.
- Invoke `& .\scripts\Smoke-Desktop.ps1` from the repository root. Add `-KeepOpen`
  to leave **only a successfully checked** smoke UI open. Paths are derived from
  the script location, so invocation by absolute path works from another directory.
  Do not dot-source the smoke script; it owns its exit status.
- Do not interact with, move, resize, minimize, or obscure the smoke window with
  dialogs during the run. Supply no credentials and create no connections.
  PowerShell's host UI Automation and System.Drawing assemblies must be available;
  the app's newer runtime assemblies are not loaded into the PowerShell host.

The exit status is **0 only after assertions, capture, and cleanup (or intentional
KeepOpen) succeed**, otherwise **1**. This script owns its failure handling; it is
not hooked into the build, package, or existing test entry points.

## Safety boundary verified against source

- [Desktop startup](../../src/ContainerToDrive.Desktop/App.xaml.cs) passes
  `allowStart: !elevated` to `ControllerSession`.
- [ControllerSession](../../src/ContainerToDrive.Desktop/ControllerSession.cs) skips
  `EnsureStartedAsync` in that mode. It can still **attempt Status IPC**, so the
  elevated UI is not an offline/network-sandbox guarantee.
- [AppPaths](../../src/ContainerToDrive.Windows/AppPaths.cs) accepts `--data-root` and
  incorporates its normalized path, Windows user SID, and session ID in the pipe
  identity. Each run gets a previously nonexistent, empty GUID directory below
  the repository's .local/desktop-smoke directory; no installed profile root is
  used or copied. No controller is started for this root by the script.
- [ControllerClient](../../src/ContainerToDrive.Windows/ControllerClient.cs) also rejects
  elevated controller startup. Construction and Status sends do not create store
  directories. In this restricted run, the entire root must remain empty.
- [MainWindow](../../src/ContainerToDrive.Desktop/MainWindow.xaml.cs) displays the
  administrator banner and prevents mounting in elevated mode. Without a controller
  snapshot, **Add connection is disabled**. Close hides to the tray; explicit Exit
  has a confirmation and can leave independently running drives alone.
- The [application manifest](../../src/ContainerToDrive.Desktop/app.manifest) uses
  `asInvoker`. The smoke launches only the exact published Desktop executable,
  without a shell or elevation verb, and removes inherited cloud/app/runtime
  overrides from the **child's** environment without changing the caller's.

These source checks explain the safety contract; they are not binary provenance
or proof that no child ever existed. Use the preview built from the reviewed source.
The smoke checks immediate child-process **counts** at safety checkpoints, not a
machine-wide controller inventory or a continuous process trace. Existing unrelated
ContainerToDrive sessions and mounts are neither inspected nor stopped.

## What a passing run establishes

1. The launched process exposes its own visible top-level window with the exact
   native and UIA title **ContainerToDrive**. No missing/zero HWND is accepted.
2. `Process.WaitForInputIdle` is bounded at 15 seconds, with a PID-filtered
   WindowOpened event/wait handle (up to 10 seconds) for late window creation.
   WPF's false idle result with a valid HWND is not automatically a failure or a
   pass: the window must also answer a bounded 5-second window message and pass
   UIA checks rooted at `AutomationElement.FromHandle` for that owned HWND.
3. Exact-name UIA queries find one visible **Add connection** button (disabled),
   one **Exit ContainerToDrive** button (enabled, with InvokePattern), the elevated
   mounting-disabled banner and its explanatory text, and **Your connections**.
   Output contains only allowlisted labels and counts, never a raw UI tree,
   textbox values, command lines, environment dumps, SAS values, or profiles.
4. System.Drawing saves a PNG rendered by `PrintWindow` from **only that window**.
   It never captures the desktop, copies screen pixels, uses the clipboard, or
   activates unrelated windows. Failed/obviously blank capture fails the smoke;
   there is no whole-screen fallback. Provider/capture work runs on background
   MTA threads with a 40-second caller deadline. No sleeps or polling loops are
   used for initialization or exit; pixel sampling is a finite image check.
5. The isolated root remains entirely empty and the owned process has no immediate
   children at cleanup checkpoints. Any entry (including runtime, profile, intent,
   or cache state), unreadable state, or child process fails the contract and
  blocks automatic termination for the rest of that run, even if it subsequently
  disappears. This is **not** a controller-mounted-state query
   and is not a safe-eject check for a real session.

The screenshot is written to the repository-relative artifacts/evidence/desktop-smoke.png
path, which the script prints as an absolute artifact path. Cooperating invocations
serialize through a file lock in the evidence directory. A previous PNG is removed
after preflight/reservation, before launch. **A PNG's existence alone is never a
pass**: preflight failures may leave older evidence, and cleanup can fail after a
new capture. Retain the actual exit status and allowlisted console output alongside
any evidence you share. The fresh data root and evidence lock file are retained;
the script never recursively deletes application state.

## Cleanup and KeepOpen

By default, after capture the script invokes **Exit ContainerToDrive** and accepts only
the owned **Exit the interface and leave drives running?** dialog's **Exit interface
only** action. It does not use `CloseMainWindow`, which would leave the tray process
alive. Exit gets a bounded attempt (20-second process wait, 25-second caller bound).
If unavailable, hung, or not confirmed, the script rechecks the empty-root/no-child
contract, calls `Process.Kill()` on its **original owned process object**, and waits
up to 5 seconds for exit. It never uses process-tree killing or process-name stops.
The output explicitly distinguishes fallback termination from normal process exit;
fallback does **not** certify graceful shutdown or tray-icon disposal.
Unexpected exit before cleanup, or a nonzero exit without the deliberate kill
fallback, also fails the smoke.

`-KeepOpen` applies only after passing checks. Manually use **Exit ContainerToDrive** and
**Exit interface only** afterward; closing the window just hides it. If a failure
invalidates the cleanup contract, the script returns failure and reports only its
owned PID for manual inspection, preserving the root and leaving unrelated processes
alone. Do not use these forced-cleanup rules in a customer/non-elevated session.

## Explicit limits

This is **UI presentation-only smoke**, not normal-user Explorer/session certification,
a security audit, or full user-journey testing. It does not test adding/saving a
connection, SAS validation/renewal, mounting, Azure reads/writes, disconnect/recovery,
controller shutdown, cache safety, installer behavior, keyboard traversal, screen
reader usability, high contrast, multi-monitor/DPI coverage, or visual correctness
against a baseline. PrintWindow can render WPF/GPU content incompletely even when
its return value and basic nonblank check succeed; **review the PNG visually**.
The controller-unavailable UI in this deliberately elevated, empty session is
expected and must not be presented as a healthy customer session.

If a run fails, check the named phase, elevation, existing publish output, host
assemblies, desktop availability, and owned session manually. Do not relax the
ownership/state checks, fall back to screen capture, or label unexecuted journeys
as passed. No runtime result is claimed by this README.