#requires -Version 7.4
<#
.SYNOPSIS
Checks the published desktop's elevated, presentation-only shell; never builds or mounts.
.DESCRIPTION
Run in an already elevated, interactive PowerShell 7.4+ session on Windows. Only the
published Desktop executable is started, with a new repository-local data root.
No credentials or customer profiles are used. See tests/desktop/README.md for limits.
.PARAMETER KeepOpen
Leave the owned desktop open only after all presentation checks and capture succeed.
#>
[CmdletBinding()]
param([switch] $KeepOpen)

. (Join-Path $PSScriptRoot 'Common.ps1')

function Assert-SmokeEmptyState {
    param([Parameter(Mandatory)][string] $DataRoot, [Parameter(Mandatory)][Diagnostics.Process] $Process)
    if ($script:SmokeUnsafeStateObserved) { throw 'An earlier state check failed; automatic termination remains blocked.' }
    try {
        $null = Assert-WorkspacePath $DataRoot
        if (-not (Test-Path -LiteralPath $DataRoot -PathType Container) -or
            @(Get-ChildItem -LiteralPath $DataRoot -Force -ErrorAction Stop).Count -ne 0) {
            throw 'The fresh smoke root is no longer empty; automatic termination is not permitted.'
        }
        # No global process-name search, command lines, or controller IPC. The current
        # elevated Desktop never starts a controller, and even its Status calls do not
        # create persistence. Any child or root entry invalidates this cleanup contract.
        if (-not $Process.HasExited) {
            $children = @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $($Process.Id)" `
                -Property ProcessId -OperationTimeoutSec 5 -ErrorAction Stop)
            if ($children.Count -ne 0) { throw 'The smoke process has children; automatic termination is not permitted.' }
        }
    }
    catch {
        # A disappearing child/file must not turn an already unsafe run back into
        # one eligible for forced cleanup. Read/access errors also fail closed.
        $script:SmokeUnsafeStateObserved = $true
        throw
    }
}

$script:SmokeUnsafeStateObserved = $false
$owned = $null
$started = $false
$dataRoot = $null
$inspection = $null
$helper = $null
$evidenceLock = $null
$evidencePath = $null
$captured = $false
$passed = $false
$failed = $false
$cleanup = 'not started'
$phase = 'Windows/elevation preflight'
try {
    if (-not $IsWindows -or -not [Environment]::UserInteractive) {
        throw 'An interactive Windows desktop is required.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'This presentation-only smoke requires an already elevated session; it never elevates itself.'
        }
    }
    finally { $identity.Dispose() }

    $phase = 'Repository and existing self-contained preview preflight'
    $null = Assert-WorkspacePath $script:RepositoryRoot -AllowRoot
    if (-not (Test-Path -LiteralPath (Get-WorkspacePath 'ContainerToDrive.slnx') -PathType Leaf)) {
        throw 'Repository marker is missing.'
    }
    $payload = Get-WorkspacePath 'artifacts/publish/ContainerToDrive'
    # Presence checks, not a rebuild, signature assertion, or package certification.
    foreach ($name in @('ContainerToDrive.Desktop.exe', 'ContainerToDrive.Desktop.dll',
            'ContainerToDrive.Desktop.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
        $candidate = Assert-WorkspacePath (Join-Path $payload $name)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'The published preview is incomplete.' }
    }
    $executable = Assert-WorkspacePath (Join-Path $payload 'ContainerToDrive.Desktop.exe')
    $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($script:RepositoryRoot))
    if ($volume.DriveType -ne [IO.DriveType]::Fixed -or $volume.DriveFormat -ne 'NTFS') {
        throw 'The isolated data root requires a fixed local NTFS volume.'
    }

    $phase = 'Host UI Automation and window-capture helper initialization'
    # Use the PowerShell host's desktop assemblies, NOT the app's .NET runtime.
    # Add-Type compiles this helper in memory; it does not build any app project.
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing.Common
    $desktopReferences = @(
        [System.Windows.Automation.AutomationElement].Assembly.Location
        [System.Windows.Automation.AutomationEvent].Assembly.Location
        [System.Windows.Rect].Assembly.Location
        [System.Drawing.Bitmap].Assembly.Location
        (Join-Path $PSHOME 'System.Private.Windows.GdiPlus.dll')
        (Join-Path $PSHOME 'System.Private.Windows.Core.dll')
    ) | Select-Object -Unique
    $referenceNames = @($desktopReferences | ForEach-Object { [IO.Path]::GetFileName($_) })
    $references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File) |
        Where-Object { $_.Name -notin $referenceNames } | ForEach-Object { $_.FullName }) + $desktopReferences
    # A unique namespace avoids silently reusing an older helper after script edits.
    $helperSource = @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace DesktopSmoke_NAMESPACE
{
    public sealed class Inspection
    {
        public IntPtr Window;
        public bool InputIdle;
        public int AddCount, ExitCount, BannerCount, ButtonCount;
        public byte[] Png;
    }

    public static class Probe
    {
        private const string Title = "ContainerToDrive";
        private const string Banner = "Administrator session — mounting is disabled";
        private const string BannerBody = "Exit this interface and launch ContainerToDrive normally from your desktop. Do not use Run as administrator. No elevation workaround is attempted.";
        private const string ExitDialog = "Exit the interface and leave drives running?";
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private static void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException(check);
        }

        private static bool IsOwnedWindow(IntPtr window, int processId)
        {
            uint actual;
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out actual) != 0 && actual == (uint)processId;
        }

        private static bool HasTitle(IntPtr window, string expected)
        {
            var text = new StringBuilder(256);
            return GetWindowText(window, text, text.Capacity) > 0 && text.ToString() == expected;
        }

        // UIA and PrintWindow can block in an external provider. Keep all such work
        // off the caller thread; the PowerShell caller also bounds the whole task.
        private static Task<T> OnMta<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return completion.Task;
        }

        private static AutomationElementCollection Named(AutomationElement root, string name, ControlType type)
        {
            return root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
        }

        private static AutomationElement OnlyVisible(AutomationElementCollection elements)
        {
            Require(elements.Count == 1, "Expected exactly one allowlisted accessible element.");
            var element = elements[0];
            var bounds = element.Current.BoundingRectangle;
            Require(!element.Current.IsOffscreen && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0,
                "An expected element is not visible.");
            return element;
        }

        public static Task<Inspection> InspectAsync(Process process)
        {
            return OnMta(() => Inspect(process));
        }

        private static Inspection Inspect(Process process)
        {
            int processId = process.Id;
            var opened = new ManualResetEventSlim(false);
            var gate = new object();
            bool listening = true;
            IntPtr announcedWindow = IntPtr.Zero;
            AutomationEventHandler onOpened = (sender, args) => {
                try
                {
                    var element = sender as AutomationElement;
                    if (element == null || element.Current.ProcessId != processId) return;
                    IntPtr candidate = new IntPtr(element.Current.NativeWindowHandle);
                    if (!IsOwnedWindow(candidate, processId) || !HasTitle(candidate, Title) ||
                        !IsWindowVisible(candidate) || GetAncestor(candidate, 3) != candidate) return;
                    lock (gate)
                    {
                        if (!listening) return;
                        announcedWindow = candidate;
                        opened.Set();
                    }
                }
                catch (Exception) { /* A transient event never counts as a passing assertion. */ }
            };
            bool subscribed = false;
            IntPtr window;
            bool idle = false;
            try
            {
                // Subscribe BEFORE the initial check to avoid a lost window-open event.
                // This observes PID-filtered events only, not the desktop's UI tree.
                Automation.AddAutomationEventHandler(WindowPattern.WindowOpenedEvent,
                    AutomationElement.RootElement, TreeScope.Children, onOpened);
                subscribed = true;
                try { idle = process.WaitForInputIdle(15000); }
                catch (InvalidOperationException) { Require(!process.HasExited, "Desktop exited before UI initialization."); }
                process.Refresh();
                window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    Require(opened.Wait(10000), "No owned main window appeared before the deadline.");
                    process.Refresh();
                    window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) { lock (gate) { window = announcedWindow; } }
                }
            }
            finally
            {
                lock (gate) { listening = false; }
                try
                {
                    if (subscribed) Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent,
                        AutomationElement.RootElement, onOpened);
                }
                finally { opened.Dispose(); }
            }

            Require(!process.HasExited && IsOwnedWindow(window, processId) && IsWindowVisible(window) &&
                GetAncestor(window, 3) == window && HasTitle(window, Title), "The owned main window is missing or unexpected.");
            // WPF can return false from Process.WaitForInputIdle despite an existing
            // HWND. Never treat either outcome alone as success: require a bounded
            // WM_NULL response AND the actual HWND's UI Automation assertions.
            UIntPtr response;
            Require(SendMessageTimeout(window, 0, UIntPtr.Zero, IntPtr.Zero, 0x0002 | 0x0020,
                5000, out response) != IntPtr.Zero, "The main window did not respond.");
            var root = AutomationElement.FromHandle(window);
            Require(root.Current.ProcessId == processId && root.Current.Name == Title &&
                root.Current.ControlType == ControlType.Window && !root.Current.IsOffscreen,
                "The UIA root does not identify the owned main window.");
            var add = Named(root, "Add connection", ControlType.Button);
            var exit = Named(root, "Exit ContainerToDrive", ControlType.Button);
            var banner = Named(root, Banner, ControlType.Text);
            Require(!OnlyVisible(add).Current.IsEnabled, "Add connection must be disabled without a controller snapshot.");
            var exitButton = OnlyVisible(exit);
            Require(exitButton.Current.IsEnabled, "Exit ContainerToDrive must be enabled.");
            Require(exitButton.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern, "Exit must expose InvokePattern.");
            OnlyVisible(banner);
            OnlyVisible(Named(root, BannerBody, ControlType.Text));
            OnlyVisible(Named(root, "Your connections", ControlType.Text));
            int buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Count;
            byte[] png = CaptureWindow(window, processId);
            Require(!process.HasExited && IsOwnedWindow(window, processId), "Desktop exited during capture.");
            return new Inspection { Window = window, InputIdle = idle, AddCount = add.Count,
                ExitCount = exit.Count, BannerCount = banner.Count, ButtonCount = buttons, Png = png };
        }

        private static byte[] CaptureWindow(IntPtr window, int processId)
        {
            // PrintWindow renders ONLY this HWND into a private bitmap. No screen
            // DC, desktop bitmap, CopyFromScreen, clipboard, or foreground switching.
            IntPtr previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Require(previousDpi != IntPtr.Zero, "Per-monitor capture coordinates are unavailable.");
            try
            {
                Require(IsOwnedWindow(window, processId), "Capture window ownership changed.");
                Rect rect;
                Require(GetWindowRect(window, out rect), "Window bounds are unavailable.");
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                Require(width > 0 && height > 0 && width <= 8192 && height <= 8192 &&
                    (long)width * height <= 32000000, "Window bounds are invalid or too large.");
                // GDI does not preserve alpha; RGB prevents a transparent PNG.
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Black);
                        IntPtr dc = graphics.GetHdc();
                        try { Require(PrintWindow(window, dc, 2), "Window-only capture failed."); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    Rect after;
                    Require(GetWindowRect(window, out after) && rect.Left == after.Left && rect.Top == after.Top &&
                        rect.Right == after.Right && rect.Bottom == after.Bottom, "Window moved during capture.");
                    // Reject an obviously blank render; this is not a visual-diff test.
                    int sample = bitmap.GetPixel(width / 4, height / 4).ToArgb();
                    bool varied = false;
                    for (int y = 1; y < 8; y++)
                        for (int x = 1; x < 8; x++)
                            varied |= bitmap.GetPixel(width * x / 8, height * y / 8).ToArgb() != sample;
                    Require(varied, "Window capture is blank; no screen-capture fallback is permitted.");
                    using (var output = new MemoryStream())
                    {
                        bitmap.Save(output, ImageFormat.Png);
                        return output.ToArray();
                    }
                }
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        }

        public static Task<bool> ExitAsync(Process process, IntPtr mainWindow)
        {
            return OnMta(() => {
                int processId = process.Id;
                // The isolated elevated session has no controller snapshot. Accept
                // ONLY its exact interface-only confirmation, never a shutdown,
                // force-disconnect, or other application's dialog.
                AutomationEventHandler confirm = (sender, args) => {
                    try
                    {
                        var dialog = sender as AutomationElement;
                        if (dialog == null || dialog.Current.ProcessId != processId || process.HasExited) return;
                        IntPtr window = new IntPtr(dialog.Current.NativeWindowHandle);
                        if (!IsOwnedWindow(window, processId) || !HasTitle(window, ExitDialog) ||
                            GetAncestor(window, 3) != mainWindow) return;
                        var accept = OnlyVisible(Named(dialog, "Exit interface only", ControlType.Button));
                        if (accept.Current.IsEnabled) ((InvokePattern)accept.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    }
                    catch (Exception) { /* Caller checks actual process exit; otherwise guarded fallback. */ }
                };
                Automation.AddAutomationEventHandler(WindowPattern.WindowOpenedEvent,
                    AutomationElement.RootElement, TreeScope.Subtree, confirm);
                try
                {
                    Require(IsOwnedWindow(mainWindow, processId) && HasTitle(mainWindow, Title), "Owned exit window is unavailable.");
                    var root = AutomationElement.FromHandle(mainWindow);
                    var exit = OnlyVisible(Named(root, "Exit ContainerToDrive", ControlType.Button));
                    ((InvokePattern)exit.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    return process.WaitForExit(20000);
                }
                finally
                {
                    Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent,
                        AutomationElement.RootElement, confirm);
                }
            });
        }
    }
}
'@
    $helperSource = $helperSource.Replace('DesktopSmoke_NAMESPACE', ('DesktopSmoke_' + [guid]::NewGuid().ToString('N')))
    $helper = Add-Type -TypeDefinition $helperSource -ReferencedAssemblies $references -PassThru |
        Where-Object { $_.Name -eq 'Probe' }

    $phase = 'Isolated data root and evidence reservation'
    $null = New-WorkspaceDirectory 'artifacts/evidence'
    $evidencePath = Get-WorkspacePath 'artifacts/evidence/desktop-smoke.png'
    $lockPath = Get-WorkspacePath 'artifacts/evidence/desktop-smoke.lock'
    # Serialize cooperating smoke invocations sharing the single screenshot path.
    $evidenceLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if (Test-Path -LiteralPath $evidencePath) {
        if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw 'Expected a screenshot file.' }
        Remove-Item -LiteralPath $evidencePath -ErrorAction Stop
    }
    $relativeRoot = '.local/desktop-smoke/' + [guid]::NewGuid().ToString('N')
    $dataRoot = Get-WorkspacePath $relativeRoot
    if (Test-Path -LiteralPath $dataRoot) { throw 'The smoke data root must be new, never reused.' }
    $null = New-WorkspaceDirectory $relativeRoot
    if (@(Get-ChildItem -LiteralPath $dataRoot -Force -ErrorAction Stop).Count -ne 0) { throw 'The smoke root must be empty.' }

    $phase = 'Launching the exact published desktop'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $payload
    $start.ArgumentList.Add('--data-root')
    $start.ArgumentList.Add($dataRoot)
    # Child-only cleanup of inherited cloud/app/runtime overrides. Preserve normal
    # Windows environment, including PATH, PATHEXT, SystemRoot, and ComSpec.
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(CONTAINERTODRIVE_|BLOBTODRIVE_|RCLONE_|AZURE_|ARM_|AWS_|GOOGLE_|GCLOUD_|DOTNET_|CORECLR_|COR_)') {
            $null = $start.Environment.Remove($key)
        }
    }
    $owned = [Diagnostics.Process]::new()
    $owned.StartInfo = $start
    $started = $owned.Start()
    if (-not $started) { throw 'Desktop did not start.' }
    # Retain the original process handle for cleanup; never re-open a PID by name.
    $null = $owned.Handle
    Write-Host "Owned smoke PID: $($owned.Id)"

    $phase = 'Bounded main-window response, allowlisted UIA assertions, and window-only capture'
    $pending = $helper::InspectAsync($owned)
    if (-not $pending.Wait(40000)) { throw 'Desktop inspection timed out.' }
    $inspection = $pending.GetAwaiter().GetResult()
    $phase = 'No app-mounted state / no child-process assertion'
    Assert-SmokeEmptyState -DataRoot $dataRoot -Process $owned

    $phase = 'Writing the window-only PNG'
    $null = Assert-WorkspacePath $evidencePath
    $output = [IO.File]::Open($evidencePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $output.Write($inspection.Png, 0, $inspection.Png.Length); $output.Flush($true) }
    finally { $output.Dispose() }
    $captured = $true
    $passed = $true
}
catch {
    # UIA/provider/transport exceptions can contain unapproved UI text. Do not
    # print exception objects, environment values, command lines, or UI trees.
    [Console]::Error.WriteLine("FAIL: $phase. Details suppressed to avoid exposing non-allowlisted data; see tests/desktop/README.md.")
    if (-not $started) {
        # Before any app starts there is no UI/customer data; compiler/setup
        # diagnostics are necessary to locate a broken host helper.
        [Console]::Error.WriteLine($_.Exception.Message)
        [Console]::Error.WriteLine($_.ScriptStackTrace)
    }
    $failed = $true
}
finally {
    if ($started) {
        try {
            Assert-SmokeEmptyState -DataRoot $dataRoot -Process $owned
            if ($passed -and $owned.HasExited) { throw 'Desktop exited unexpectedly before cleanup.' }
            if ($KeepOpen -and $passed) {
                $cleanup = "kept open by request (owned PID $($owned.Id))"
            }
            else {
                if (-not $owned.HasExited -and $null -ne $inspection) {
                    # CloseMainWindow would hide to tray, not exit. Try the exact
                    # UIA Exit action and its interface-only confirmation instead.
                    try {
                        $exiting = $helper::ExitAsync($owned, $inspection.Window)
                        if ($exiting.Wait(25000)) { $null = $exiting.GetAwaiter().GetResult() }
                    }
                    catch { } # Not a pass: actual exit or guarded fallback is required below.
                }
                if (-not $owned.HasExited) {
                    Assert-SmokeEmptyState -DataRoot $dataRoot -Process $owned
                    # Safe ONLY under this script's fresh-root/elevated/no-child
                    # contract. Never Kill(true), Stop-Process -Name, or a PID scan.
                    $owned.Kill()
                    if (-not $owned.WaitForExit(5000)) { throw 'Owned desktop did not terminate.' }
                    $cleanup = 'owned desktop terminated after guarded fallback (graceful Exit not confirmed)'
                }
                else {
                    if ($passed -and $owned.ExitCode -ne 0) { throw 'Desktop exited abnormally.' }
                    $cleanup = 'owned desktop exited; no process-tree termination used'
                }
                Assert-SmokeEmptyState -DataRoot $dataRoot -Process $owned
            }
        }
        catch {
            $failed = $true
            [Console]::Error.WriteLine("FAIL: cleanup refused or incomplete for owned PID $($owned.Id). Inspect that session manually; no unrelated process was stopped and the data root was retained.")
        }
    }
    if ($null -ne $owned) { $owned.Dispose() }
    if ($null -ne $evidenceLock) { $evidenceLock.Dispose() }
}

if ($captured) { Write-Host "Window-only evidence: $evidencePath" }
if ($failed -or -not $passed) { exit 1 }
Write-Host 'PASS: elevated UI presentation-only smoke; not customer-session or user-journey certification.'
Write-Host 'Window title: ContainerToDrive'
Write-Host "UIA name: Add connection; matches=$($inspection.AddCount); enabled=False"
Write-Host "UIA name: Exit ContainerToDrive; matches=$($inspection.ExitCount); enabled=True"
Write-Host "UIA name: Administrator session — mounting is disabled; matches=$($inspection.BannerCount)"
Write-Host "UIA button count (names not enumerated): $($inspection.ButtonCount)"
Write-Host "Process.WaitForInputIdle returned: $($inspection.InputIdle); bounded window-message response and UIA checks passed."
Write-Host "Cleanup: $cleanup"
exit 0