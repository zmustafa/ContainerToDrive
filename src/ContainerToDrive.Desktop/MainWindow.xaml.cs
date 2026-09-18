using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ControllerSession _session;
    private readonly bool _elevated;
    private readonly DesktopPreferencesStore _preferencesStore;
    private readonly bool _preferencesReadable;
    private DesktopPreferences _preferences;
    private readonly WindowsStartup? _windowsStartup;
    private bool _windowsStartupAvailable;
    private bool _startWithWindows;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _capabilityCollapseTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Stopwatch _contactAge = new();
    private CancellationTokenSource _pollStop = new();
    private CancellationTokenSource? _actionCancellation;
    private Task? _pollTask;
    private readonly EndpointResolver _endpointResolver = new();
    private Task? _endpointRefreshTask;
    private AppSnapshot? _snapshot;
    private bool _statisticsLoading;
    private DateTimeOffset? _lastContact;
    private bool _online;
    private bool _busy;
    private bool _installerRunning;
    private bool _relocating;
    private DateTimeOffset? _capabilitiesCheckedAt;
    private bool _exitInProgress;
    private bool _hiddenNoticeShown;
    private string _view = "Connections";
    private string _notice = "";
    private string _exportPath = "No export created in this session.";

    internal MainWindow(ControllerSession session, string dataRoot, bool elevated)
    {
        _session = session;
        DataRoot = dataRoot;
        _elevated = elevated;
        _preferencesStore = new(dataRoot);
        _preferencesReadable = _preferencesStore.TryLoad(out _preferences);
        Preferences = new(_preferences);
        _endpointResolver.RefreshSeconds = _preferences.DnsRefreshSeconds;
        Statistics.Period = Statistics.Periods.Single(period => period.Days == _preferences.ActivityPeriodDays);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ContainerToDrive.Desktop.exe");
            _windowsStartup = new WindowsStartup(executable, dataRoot);
            _startWithWindows = _windowsStartup.IsEnabled();
            _windowsStartupAvailable = File.Exists(executable);
        }
        catch (Exception) { _windowsStartupAvailable = false; }
        InitializeComponent();
        DataContext = this;
        ConnectionsNav.IsChecked = true;
        _session.SnapshotReceived += OnSnapshot;
        _session.Unavailable += MarkUnavailable;
        _clock.Tick += OnClockTick;
        _capabilityCollapseTimer.Tick += OnCapabilityCollapseTick;
        Statistics.SelectionChanged += OnStatisticsSelectionChanged;
        Preferences.PropertyChanged += OnPreferencesChanged;
    }

    public PreferencesEditor Preferences { get; }
    internal bool StartMinimized => _preferences.StartMinimized;
    public bool CanEditPreferences => NotBusy && !IsElevated && _preferencesReadable;
    public bool CanSavePreferences => CanEditPreferences && Preferences.HasChanges && !Preferences.HasError;
    public string PreferencesStatus => !_preferencesReadable ? "Saved preferences could not be read. Session defaults are in use; the saved file has been preserved."
        : IsElevated ? "Change preferences from a normal Windows session."
        : Preferences.HasChanges ? "Unsaved preferences" : "Preferences saved";
    public ObservableCollection<ProfileCard> Profiles { get; } = [];
    public double ConnectionCardWidth { get; private set; } = double.NaN;
    public ObservableCollection<CapabilityCheck> Capabilities { get; } = [];
    public TransferDashboard Statistics { get; } = new();
    public SessionLog Logs => _session.Log;
    public IReadOnlyList<ComponentStatus> Components => SystemStatus.Describe(_snapshot, HasCurrentSnapshot);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DataRoot { get; }
    public bool StartWithWindows => _startWithWindows;
    public bool CanChangeWindowsStartup => NotBusy && !IsElevated && _windowsStartupAvailable;
    public string WindowsStartupHint => IsElevated ? "Change startup settings from a normal Windows session."
        : !_windowsStartupAvailable ? "Startup settings are unavailable. Check the application location and Windows policy."
        : "Start in the tray when you sign in. Connections with automatic mounting enabled will reconnect.";
    public bool Busy => _busy;
    public bool CanCancelRequest => Busy && !_installerRunning && !_relocating;
    public bool CanChangeDataLocation => CanInteract && !IsElevated && DataRelocation.CanRelocate(_snapshot);
    public string DataLocationHint => IsElevated ? "Change the data folder from a normal Windows session."
        : !HasCurrentSnapshot ? "Connect to the controller before changing the data folder."
        : !DataRelocation.CanRelocate(_snapshot) ? "Disconnect all drives and resolve recovery before changing the data folder."
        : "Browse for an empty local folder. Copy existing data and reopen the app there.";
    public bool HasCapabilities => Capabilities.Count != 0;
    public string CapabilitySummary => $"Last test: {DisplayText.Time(_capabilitiesCheckedAt)} · {Capabilities.Count(check => check.Passed)} of {Capabilities.Count} checks passed";
    public bool CanInstallDependencies => NotBusy && SystemStatus.CanInstall(_snapshot, HasCurrentSnapshot, IsElevated);
    public string InstallDependenciesHint => IsElevated ? "Restart the app normally before dependency setup."
        : !HasCurrentSnapshot ? "Connect to the backend before dependency setup."
        : _snapshot is { EngineAvailable: true, WinFspInstalled: true } ? "All dependencies are installed."
        : !SystemStatus.CanInstall(_snapshot, HasCurrentSnapshot, IsElevated) ? "Disconnect all drives before dependency setup."
        : "Install missing dependencies from verified upstream packages.";
    public bool NotBusy => !_busy && !_exitInProgress;
    private bool HasCurrentSnapshot => _online && _contactAge.IsRunning && _contactAge.Elapsed < TimeSpan.FromSeconds(20);
    public bool CanInteract => NotBusy && HasCurrentSnapshot;
    public bool IsEmpty => _snapshot is not null && Profiles.Count == 0;
    public string Notice => _notice;
    public bool HasNotice => _notice.Length != 0;
    public string ExportPath => _exportPath;
    public bool ShowAddConnection => _view != "Settings";
    public string ViewTitle => _view switch { "Activity" => "Activity & recovery", "Diagnostics" => "Diagnostics", "Logs" => "Logs", "Settings" => "Settings", _ => "Your connections" };
    public string ViewSubtitle => _view switch
    {
        "Activity" => "Transfers, history, and connection health",
        "Diagnostics" => "Review local state without sending your data anywhere.",
        "Logs" => "Current session",
        "Settings" => "Application preferences and connection configuration",
        _ => _snapshot is null ? "Connecting to your local controller…" : $"{Profiles.Count} connections"
    };
    public string ModeText => _snapshot?.WritableEnabled == true ? "Writable mounts available" : "Read-only mounts only";
    private bool IsElevated => _elevated || _snapshot?.Elevated == true;
    public bool HasBanner => IsElevated || !HasCurrentSnapshot ||
        _snapshot is { WinFspInstalled: false } or { EngineAvailable: false } || _snapshot?.WritableEnabled != true;
    public string BannerTitle => IsElevated ? "Administrator session — mounting is disabled"
        : !HasCurrentSnapshot ? "Controller status is unavailable or stale"
        : _snapshot is { WinFspInstalled: false } or { EngineAvailable: false } ? "Finish installing the prerequisites"
        : _snapshot?.WritableEnabled == true ? "New connections begin read-only" : "Read-only mounts only";
    public string BannerText => IsElevated ? "Exit this interface and launch ContainerToDrive normally from your desktop. Do not use Run as administrator. No elevation workaround is attempted."
        : !HasCurrentSnapshot ? "Drive and upload states must be treated as unknown. Existing drives may still be running. Refresh to reconnect; no worker is stopped by an interface error."
        : _snapshot is { WinFspInstalled: false } or { EngineAvailable: false } ? "Required components are missing or unverified. Dependency setup is available in Settings."
        : _snapshot?.WritableEnabled == true ? "Writable access is available only after explicit confirmation. Writes and deletions affect Azure; keep the cache intact and avoid concurrent editors."
        : "This alpha is intended for browsing and copying files out. Azure access and retrieval charges can apply. Local cached files can remain after credentials expire.";
    public string ContactText => (HasCurrentSnapshot ? "Controller connected" : "Controller unavailable or stale") + " · Last response: " + DisplayText.Time(_lastContact) + (_preferences.CloseToTray ? " · Close → tray" : " · Close → exit confirmation");

    private void OnConnectionCardsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged) return;
        const double minimumCardWidth = 480;
        const double cardMargin = 12;
        var columns = args.NewSize.Width >= 2 * (minimumCardWidth + cardMargin) ? 2 : 1;
        ConnectionCardWidth = args.NewSize.Width / columns;
        PropertyChanged?.Invoke(this, new(nameof(ConnectionCardWidth)));
    }

    internal void StartPolling()
    {
        _clock.Start();
        if (_pollTask is null) _pollTask = PollAsync(_pollStop.Token);
    }

    internal void StopPolling()
    {
        _clock.Stop();
        _capabilityCollapseTimer.Stop();
        _pollStop.Cancel();
        _actionCancellation?.Cancel();
    }

    // A display-only clock expires observations even while a long controller operation is pending.
    // It performs no I/O and cannot overlap control requests.
    private void OnClockTick(object? sender, EventArgs e) => Changed();

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var response = await _session.SendAsync(new Request { Operation = "Status" }, token);
                if (!response.Success || response.Snapshot is null) MarkUnavailable();
                else RefreshEndpoints(token);
                if (_view == "Activity") await LoadStatisticsAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { MarkUnavailable(); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private void OnSnapshot(AppSnapshot snapshot)
    {
        _snapshot = snapshot;
        Statistics.UpdateConnections(snapshot);
        _online = true;
        _lastContact = DateTimeOffset.Now;
        _contactAge.Restart();
        var ids = snapshot.Profiles.Select(p => p.Id).ToHashSet();
        for (var i = Profiles.Count - 1; i >= 0; i--)
            if (!ids.Contains(Profiles[i].Profile.Id)) Profiles.RemoveAt(i);
        foreach (var profile in snapshot.Profiles)
        {
            var card = Profiles.FirstOrDefault(p => p.Profile.Id == profile.Id);
            if (card is null) { card = new ProfileCard(); Profiles.Add(card); }
            UpdateCard(card, profile);
        }
        Changed();
    }

    private void RefreshEndpoints(CancellationToken token)
    {
        if (!_preferences.ShowEndpointInfo) return;
        if (_endpointRefreshTask is null || _endpointRefreshTask.IsCompleted)
            _endpointRefreshTask = RefreshEndpointsAsync(token);
    }

    private async Task RefreshEndpointsAsync(CancellationToken token)
    {
        var profiles = Profiles.Select(card => card.Profile).GroupBy(profile => profile.Endpoint.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        await Task.WhenAll(profiles.Select(async profile =>
        {
            try
            {
                var result = await _endpointResolver.ResolveAsync(profile, token);
                if (token.IsCancellationRequested || !_preferences.ShowEndpointInfo) return;
                foreach (var card in Profiles) card.SetEndpoint(result);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { }
        }));
    }

    private void UpdateCard(ProfileCard card, Profile profile)
    {
        var mount = _snapshot?.Mounts.FirstOrDefault(m => m.ProfileId == profile.Id);
        var canMount = !IsElevated && _snapshot is { WinFspInstalled: true, EngineAvailable: true } &&
            (profile.ReadOnly || _snapshot.WritableEnabled);
        card.Update(profile, mount, HasCurrentSnapshot, canMount, _busy || _exitInProgress, _snapshot?.Transfers.FirstOrDefault(summary => summary.ProfileId == profile.Id), _preferences.ShowEndpointInfo);
    }

    private void Changed()
    {
        Statistics.SetOnline(HasCurrentSnapshot);
        foreach (var card in Profiles) UpdateCard(card, card.Profile);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void MarkUnavailable() { _online = false; Changed(); }
    private void SetNotice(string value) { _notice = value; Changed(); }
    private void SetBusy(bool value) { _busy = value; Changed(); }
    private static ProfileCard? Card(object sender) => (sender as FrameworkElement)?.DataContext as ProfileCard;

    private async void OnNavigation(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string view } || ConnectionsView is null) return;
        _view = view;
        ConnectionsView.Visibility = view == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = view == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = view == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = view == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        Changed();
        if (view == "Activity") await LoadStatisticsAsync(_pollStop.Token);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => SettingsNav.IsChecked = true;

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, new(string.Empty));

    private void OnRestorePreferencesClick(object sender, RoutedEventArgs e)
    {
        if (CanEditPreferences) Preferences.RestoreDefaults();
    }

    private void OnSavePreferencesClick(object sender, RoutedEventArgs e)
    {
        if (!CanSavePreferences) return;
        try
        {
            var preferences = Preferences.Build();
            _preferencesStore.Save(preferences);
            var previous = _preferences;
            _preferences = preferences;
            Preferences.MarkSaved(preferences);
            _endpointResolver.RefreshSeconds = preferences.DnsRefreshSeconds;
            if (previous.DnsRefreshSeconds != preferences.DnsRefreshSeconds || previous.ShowEndpointInfo != preferences.ShowEndpointInfo)
                _endpointResolver.Invalidate();
            if (previous.ActivityPeriodDays != preferences.ActivityPeriodDays)
                Statistics.Period = Statistics.Periods.Single(period => period.Days == preferences.ActivityPeriodDays);
            _capabilityCollapseTimer.Stop();
            if (CapabilityResults.IsExpanded && HasCapabilities) StartCapabilityCollapseTimer();
            SetNotice("Preferences saved. Existing connection settings and running drives were not changed.");
        }
        catch (Exception)
        {
            SetNotice("Preferences could not be saved. Current settings remain in use; check data-folder permissions and try again.");
        }
    }

    private async void OnStatisticsSelectionChanged()
    {
        if (_view == "Activity") await LoadStatisticsAsync(_pollStop.Token);
    }

    private async Task LoadStatisticsAsync(CancellationToken token)
    {
        if (_statisticsLoading || token.IsCancellationRequested) return;
        _statisticsLoading = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var profileId = Statistics.ProfileId;
                var days = Statistics.Period.Days;
                Statistics.SetLoading();
                var response = await _session.SendAsync(new Request { Operation = "Statistics", ProfileId = profileId, StatisticsDays = days }, token);
                if (profileId != Statistics.ProfileId || days != Statistics.Period.Days) continue;
                if (response.Success && response.Statistics is { } report) Statistics.Update(report, DateTimeOffset.UtcNow);
                else Statistics.SetUnavailable();
                break;
            }
        }
        catch (Exception) { Statistics.SetUnavailable(); }
        finally { _statisticsLoading = false; }
    }

    private async Task<Response?> RunAsync(Request request, string success)
    {
        if (!NotBusy) return null;
        using var cancellation = new CancellationTokenSource();
        _actionCancellation = cancellation;
        SetBusy(true);
        SetNotice(request.Operation == "RelocateData" ? "Copying and verifying application data. Keep the app running; the original folder will be retained." : "Contacting the controller…");
        try
        {
            var response = await _session.SendAsync(request, cancellation.Token);
            if (!response.Success) SetNotice(DisplayText.Failure(request.Operation, request.RequestId));
            else SetNotice(success);
            return response;
        }
        catch (OperationCanceledException)
        {
            MarkUnavailable();
            SetNotice("The request was cancelled or timed out. The controller may still complete it; refresh status before retrying. " + $"Request {request.RequestId:N}.");
            return null;
        }
        catch (Exception)
        {
            MarkUnavailable();
            SetNotice(DisplayText.Failure(request.Operation, request.RequestId));
            return null;
        }
        finally { _actionCancellation = null; SetBusy(false); }
    }

    private void OnCancelRequest(object sender, RoutedEventArgs e) => _actionCancellation?.Cancel();

    private void OnStartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CanChangeWindowsStartup || _windowsStartup is null) return;
            _windowsStartup.SetEnabled(StartWithWindowsCheck.IsChecked == true);
            _startWithWindows = _windowsStartup.IsEnabled();
            SetNotice(_startWithWindows
                ? "Windows startup enabled. The app will start in the tray at sign-in; connections with automatic mounting enabled will reconnect."
                : "Windows startup disabled. Existing connections and running drives were not changed.");
        }
        catch (Exception)
        {
            SetNotice("The Windows startup change could not be confirmed. Check Windows permissions or policy and try again.");
        }
        finally { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartWithWindows))); }
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e) => Logs.Clear();

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _endpointResolver.Invalidate();
        var response = await RunAsync(new Request { Operation = "Status" }, "Component and connection status refreshed.");
        if (response is { Success: true, Snapshot: not null }) RefreshEndpoints(_pollStop.Token);
    }

    private void ShowCapabilities(Response? response)
    {
        Capabilities.Clear();
        Capabilities.Add(new("Frontend", true, "Desktop interface is responding."));
        if (response is { Success: true, Capabilities: { } checks })
        {
            foreach (var check in checks)
                Capabilities.Add(check.Name == "Windows session" && IsElevated
                    ? new(check.Name, false, "Restart the app normally to mount drives in Explorer.") : check);
        }
        else Capabilities.Add(new("Backend", false, "Capability testing could not reach or complete on the controller."));
        _capabilitiesCheckedAt = DateTimeOffset.Now;
        Changed();
        _capabilityCollapseTimer.Stop();
        CapabilityResults.IsExpanded = true;
        StartCapabilityCollapseTimer();
    }

    private void StartCapabilityCollapseTimer()
    {
        if (_preferences.CapabilityCollapseSeconds == 0) return;
        _capabilityCollapseTimer.Interval = TimeSpan.FromSeconds(_preferences.CapabilityCollapseSeconds);
        _capabilityCollapseTimer.Start();
    }

    private void OnCapabilityCollapseTick(object? sender, EventArgs e)
    {
        _capabilityCollapseTimer.Stop();
        CapabilityResults.IsExpanded = false;
    }

    private void OnCapabilityResultsCollapsed(object sender, RoutedEventArgs e) => _capabilityCollapseTimer.Stop();

    private async void OnTestCapabilitiesClick(object sender, RoutedEventArgs e)
    {
        if (!NotBusy) return;
        _capabilityCollapseTimer.Stop();
        CapabilityResults.IsExpanded = true;
        var response = await RunAsync(new Request { Operation = "TestCapabilities" }, "Local capability checks completed. Azure access and writes were not tested.");
        ShowCapabilities(response);
    }

    private async void OnInstallDependenciesClick(object sender, RoutedEventArgs e)
    {
        if (!CanInstallDependencies) return;
        if (!ConfirmationWindow.Ask(this, "Install missing dependencies?",
            $"Download verified rclone {DependencySetup.EngineVersion} and/or the official WinFsp {DependencySetup.WinFspVersion} installer as needed.\n\n" +
            "The engine is installed for your Windows user. WinFsp setup opens separately and may require Windows administrator approval or a restart. Review its license in the installer. Existing drives and cached data will not be removed.", "Install dependencies")) return;
        if (!CanInstallDependencies) return;
        using var cancellation = new CancellationTokenSource();
        _capabilityCollapseTimer.Stop();
        _actionCancellation = cancellation;
        SetBusy(true);
        try
        {
            SetNotice("Checking dependencies...");
            var before = await _session.SendAsync(new Request { Operation = "TestCapabilities" }, cancellation.Token);
            if (!before.Success || before.Snapshot is null) { ShowCapabilities(before); SetNotice("Dependency status could not be verified. No installation was started."); return; }
            if (IsElevated || !SystemStatus.IsIdle(before.Snapshot))
            { SetNotice("Dependency setup requires a normal Windows session and confirmed idle drives."); return; }
            if (!before.Snapshot.EngineAvailable)
            {
                SetNotice("Downloading and verifying the rclone engine...");
                await Task.Run(() => DependencySetup.InstallEngineAsync(cancellation.Token), cancellation.Token);
            }
            var restartRequired = false;
            if (!before.Snapshot.WinFspInstalled)
            {
                SetNotice("Downloading and verifying the WinFsp installer...");
                var installer = await Task.Run(() => DependencySetup.PrepareWinFspInstallerAsync(cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")) { UseShellExecute = true };
                start.ArgumentList.Add("/i");
                start.ArgumentList.Add(installer);
                start.ArgumentList.Add("/norestart");
                using var process = Process.Start(start) ?? throw new IOException("Windows Installer could not start.");
                _installerRunning = true;
                SetNotice("Complete the WinFsp setup window. Windows may request administrator approval.");
                await process.WaitForExitAsync();
                _installerRunning = false;
                if (process.ExitCode == 1602) { SetNotice("WinFsp installation was cancelled. Dependency status will update on refresh."); return; }
                if (process.ExitCode is not (0 or 3010)) { SetNotice($"Windows Installer ended with code {process.ExitCode}. Dependency installation was not confirmed."); return; }
                restartRequired = process.ExitCode == 3010;
            }
            var after = await _session.SendAsync(new Request { Operation = "TestCapabilities" }, cancellation.Token);
            ShowCapabilities(after);
            SetNotice(restartRequired ? "WinFsp setup requires a Windows restart. No restart was initiated."
                : after is { Success: true, Snapshot: { EngineAvailable: true, WinFspInstalled: true } } ? "Dependencies are installed. Capability results are available on Connections."
                : "Dependency setup finished, but verification is incomplete. Review capability results on Connections and test again.");
        }
        catch (OperationCanceledException) { SetNotice("Dependency setup was cancelled or timed out. Refresh status before trying again."); }
        catch (Exception) { SetNotice("Dependency setup failed. Check connectivity and installation permissions, then test capabilities again. No unverified installer was launched."); }
        finally { _installerRunning = false; _actionCancellation = null; SetBusy(false); }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || _snapshot is null) return;
        var dialog = new ConnectionWindow(_session, ((App)System.Windows.Application.Current).AzureSignIn, _snapshot, null, IsElevated, _preferences) { Owner = this };
        if (dialog.ShowDialog() == true) SetNotice("Connection saved. Saving does not mount a drive; choose Mount when ready.");
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanEdit: true } card || _snapshot is null) return;
        var dialog = new ConnectionWindow(_session, ((App)System.Windows.Application.Current).AzureSignIn, _snapshot, card.Profile, IsElevated) { Owner = this };
        if (dialog.ShowDialog() == true) SetNotice("Connection updated. Settings will apply on the next mount.");
    }

    private async void OnCloneClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanClone: true } card) return;
        await RunAsync(new Request { Operation = "Clone", ProfileId = card.Profile.Id },
            "Connection cloned with a different available drive letter. The copy is not mounted and automatic mounting is off.");
    }

    private void OnRenewClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanRenew: true } card) return;
        var dialog = new CredentialWindow(_session, ((App)System.Windows.Application.Current).AzureSignIn, card.Profile) { Owner = this };
        if (dialog.ShowDialog() == true) SetNotice("The controller accepted credential renewal. Review the current mount and worker observation before continuing.");
    }

    private async void OnMountClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanMount: true } card) return;
        var recovery = card.NeedsRecovery;
        if (recovery && !ConfirmationWindow.Ask(this, "Resume this connection?",
            card.Name + " · " + card.Source + "\n\nThe previous session has uncertain recovery state. Resuming the same storage identity may upload cached changes to Azure. " +
            "Keep the existing cache intact, stop other editors, and verify this is the intended connection. This does not guarantee recovery of every open file.", "Resume connection", true)) return;
        if (!card.Profile.ReadOnly && !ConfirmationWindow.Ask(this, "Mount writable drive?",
            card.Name + " · " + card.Source + "\n\nChanges, deletions, and renames through this drive affect Azure. Uploads may be delayed; concurrent editing, databases, and multi-file atomic operations are not supported. " +
            "Use a dedicated container with appropriate data protection. An empty reported queue is not a guarantee about open files.", "Mount with writes", true)) return;
        if (!card.CanMount) return;
        await RunAsync(new Request { Operation = "Mount", ProfileId = card.Profile.Id, Confirm = recovery },
            "Mount request accepted. Open Explorer only after a current mounted observation appears.");
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanOpen: true } card) return;
        var letter = card.Profile.DriveLetter;
        if (letter.Length != 1 || letter[0] is < 'D' or > 'Z') return;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
            start.ArgumentList.Add(letter + @":\");
            using var process = Process.Start(start);
        }
        catch (Exception) { SetNotice("Explorer could not open this drive. Refresh the connection status and try again."); }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanDisconnect: true } card) return;
        if (!ConfirmationWindow.Ask(this, "Request disconnection?",
            card.Name + " (" + card.Drive + ")\n\nClose applications using this drive first. The controller will evaluate the disconnect request; this is not a guarantee that still-open files have been uploaded. " +
            "If work is pending or uncertain, keep the drive and cache available and review Activity & recovery.", "Request disconnect")) return;
        if (!card.CanDisconnect) return;
        await RunAsync(new Request { Operation = "Disconnect", ProfileId = card.Profile.Id },
            "Disconnect request accepted. Watch the reported phase; the drive may not have stopped yet.");
    }

    private async void OnForceClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanForce: true } card) return;
        if (!ConfirmationWindow.Ask(this, "Stop now — risk of unsent changes",
            card.Name + " (" + card.Drive + ")\n\n" + card.Counts + ". " + card.Observation + ".\n\n" +
            "Immediate stop can interrupt writes and leave changes only on this computer. Close all applications using the drive. " +
            "Do not delete the cache; recovery may require this same connection and credential. Unsent data may be lost. This is not a safe-eject guarantee.", "Stop now", true)) return;
        if (!card.CanForce) return;
        await RunAsync(new Request { Operation = "Disconnect", ProfileId = card.Profile.Id, Force = true, Confirm = true },
            "Immediate-stop request accepted. Preserve the cache and inspect recovery before mounting again.");
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanRemove: true } card) return;
        if (!ConfirmationWindow.Ask(this, "Remove this connection?",
            card.Name + " · " + card.Source + "\n\nThis removes the saved connection and its credential reference, not Azure blobs. Only a clean, unmounted connection can be removed. " +
            "Do not assume this securely erases locally cached contents.", "Remove connection", true)) return;
        if (!card.CanRemove) return;
        await RunAsync(new Request { Operation = "Remove", ProfileId = card.Profile.Id }, "Connection removed by the controller.");
    }

    private async void OnValidateClick(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { CanValidate: true } card) return;
        await RunAsync(new Request { Operation = "Validate", ProfileId = card.Profile.Id },
            $"Connection test passed for {card.Name}. Saved access works; write permission and access to every blob were not tested.");
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || !ConfirmationWindow.Ask(this, "Create a local diagnostic export?",
            "The controller will write its diagnostic bundle to a protected local file. It is intended to omit credentials and file contents, " +
            "but metadata may still be sensitive. Review the file before sharing. No automatic upload or clipboard copy will occur.", "Create local export")) return;
        var response = await RunAsync(new Request { Operation = "Export" }, "Diagnostic export completed. Review the protected local file before sharing it.");
        if (response is { Success: true, ExportPath: { } path })
        {
            // Display only an absolute local path; never launch a response-supplied URI or command.
            if (Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.Any(char.IsControl) && !path.Contains('?'))
                _exportPath = path;
            else SetNotice("The controller returned an unsupported export path. No file was opened or uploaded.");
            Changed();
        }
        else if (response?.Success == true) SetNotice("Export succeeded but no protected local path was returned. No file was opened or uploaded.");
    }

    private async void OnBrowseDataRootClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDataLocation) return;
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an empty application data folder", InitialDirectory = DataRoot, Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        string destination;
        bool startWithWindows;
        try
        {
            destination = DataLocation.ValidateDestination(DataRoot, picker.FolderName);
            startWithWindows = _windowsStartup?.IsEnabled() == true;
            if (startWithWindows) _ = new WindowsStartup(Path.Combine(AppContext.BaseDirectory, "ContainerToDrive.Desktop.exe"), destination);
        }
        catch (Exception)
        {
            SetNotice("Choose an empty, separate folder on a fixed local NTFS drive. Shares, linked folders, and volume roots are not supported; Windows startup also limits path length.");
            return;
        }
        if (!ConfirmationWindow.Ask(this, "Change application data folder?",
            "New location:\n" + destination + "\n\nSaved connections, protected credentials, transfer history, and cached files will be copied and verified. " +
            "The original folder will remain unchanged as a backup. Close other ContainerToDrive windows before continuing. " +
            "The app will reopen at the new location; connections with automatic mounting enabled may reconnect. Azure sign-in may need to be repeated. " +
            "Large caches can take several minutes. Keep the app running until the copy finishes.", "Copy data and reopen")) return;
        if (!CanChangeDataLocation) return;
        _relocating = true;
        Response? response;
        try { response = await RunAsync(new Request { Operation = "RelocateData", DestinationDataRoot = destination, Confirm = true }, "Data copied and verified."); }
        finally { _relocating = false; Changed(); }
        if (response is not { Success: true } || !string.Equals(response.RelocatedDataRoot, destination, StringComparison.OrdinalIgnoreCase))
        {
            SetNotice("The location change could not be confirmed. The original data is retained. A partial destination may remain; do not delete the original. Reopen the app to check the active location before retrying with an empty folder.");
            return;
        }
        StopPolling();
        if (startWithWindows)
        {
            try { new WindowsStartup(Path.Combine(AppContext.BaseDirectory, "ContainerToDrive.Desktop.exe"), destination).SetEnabled(true); }
            catch (Exception)
            {
                System.Windows.MessageBox.Show(this, "Data was copied successfully, but the Windows startup entry could not be updated. Recheck Start with Windows after reopening.", "ContainerToDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ContainerToDrive.Desktop.exe")) { UseShellExecute = false };
            start.ArgumentList.Add("--data-root");
            start.ArgumentList.Add(destination);
            using var reopened = Process.Start(start) ?? throw new IOException("The application could not be reopened.");
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(this, "Your data location was saved. Reopen ContainerToDrive normally to use it. The original folder is retained.", "ContainerToDrive", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        ((App)System.Windows.Application.Current).ExitInterface();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (((App)System.Windows.Application.Current).IsExiting) return;
        e.Cancel = true;
        if (_exitInProgress) return;
        if (!_preferences.CloseToTray) { BeginExit(); return; }
        Hide();
        if (!_hiddenNoticeShown)
        {
            ((App)System.Windows.Application.Current).NotifyHidden();
            _hiddenNoticeShown = true;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => BeginExit();

    protected override void OnClosed(EventArgs e)
    {
        StopPolling();
        _clock.Tick -= OnClockTick;
        _capabilityCollapseTimer.Tick -= OnCapabilityCollapseTick;
        _session.SnapshotReceived -= OnSnapshot;
        _session.Unavailable -= MarkUnavailable;
        Preferences.PropertyChanged -= OnPreferencesChanged;
        base.OnClosed(e);
    }

    internal async void BeginExit()
    {
        if (!NotBusy || OwnedWindows.Count > 0) return;
        _exitInProgress = true;
        Changed();
        var pollingStopped = false;
        try
        {
            Response? latest = null;
            try { latest = await _session.SendAsync(new Request { Operation = "Status" }); }
            catch (Exception) { MarkUnavailable(); }
            var snapshot = latest is { Success: true } ? latest.Snapshot : null;
            // Missing entries, faulted workers, and recovery are deliberately treated as uncertain.
            var canShutdown = snapshot is not null && snapshot.Mounts.All(m => m.Phase == MountPhase.Unmounted && !m.RecoveryRequired) &&
                snapshot.Profiles.All(p => snapshot.Mounts.Any(m => m.ProfileId == p.Id && m.Phase == MountPhase.Unmounted && !m.RecoveryRequired));
            if (!ConfirmationWindow.Ask(this, canShutdown ? "Exit ContainerToDrive?" : "Exit the interface and leave drives running?",
                canShutdown
                    ? "The controller reports no active or uncertain mounts. It will be asked to shut down. Cached files will not be cleared."
                    : "Drives are running, need recovery, or their state is unknown. Exiting removes this interface and its tray icon only. " +
                      "The controller and any drives keep running. Reopen ContainerToDrive to manage them, or cancel and disconnect each drive first.",
                canShutdown ? "Exit ContainerToDrive" : "Exit interface only")) return;

            _pollStop.Cancel();
            if (_pollTask is not null) await _pollTask;
            pollingStopped = true;
            if (canShutdown)
            {
                var stopped = false;
                try { stopped = (await _session.SendAsync(new Request { Operation = "Shutdown" })).Success; }
                catch (Exception) { MarkUnavailable(); }
                if (!stopped && !ConfirmationWindow.Ask(this, "Controller shutdown was not confirmed",
                    "State may have changed while you were confirming. Nothing will be force-stopped. Exit just the interface and leave the controller available?", "Exit interface only")) return;
            }
            ((App)System.Windows.Application.Current).ExitInterface();
        }
        finally
        {
            _exitInProgress = false;
            if (!((App)System.Windows.Application.Current).IsExiting && pollingStopped)
            {
                _pollStop.Dispose();
                _pollStop = new CancellationTokenSource();
                _pollTask = null;
                StartPolling();
            }
            Changed();
        }
    }
}