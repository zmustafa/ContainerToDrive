using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Desktop;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private MainWindow? _shell;
    private bool _handlingException;
    private AzureSignInService? _azureSignIn;
    internal AzureSignInService AzureSignIn => _azureSignIn ?? throw new InvalidOperationException("Azure sign-in is not initialized.");
    internal bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        ApplySystemColors();

        try
        {
            var dataRoot = AppPaths.ResolveDataRoot(e.Args);
            _azureSignIn = new AzureSignInService(dataRoot);
            var elevated = AppPaths.IsElevated;
            var session = new ControllerSession(new ControllerClient(dataRoot), allowStart: !elevated);
            _shell = new MainWindow(session, dataRoot, elevated);
            MainWindow = _shell;
            CreateTray();

            // No mount process belongs to this UI. Its lifetime is independent.
            if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) && !_shell.StartMinimized)
                _shell.Show();
            _shell.StartPolling();
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(
                "ContainerToDrive could not open. Check the installation and launch it as a standard Windows user. " +
                "No running controller or drive has been stopped.",
                "ContainerToDrive", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitInterface();
        }
    }

    private void CreateTray()
    {
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Open ContainerToDrive", null, (_, _) => { _ = Dispatcher.InvokeAsync(ShowShell); });
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Exit ContainerToDrive…", null, (_, _) => { _ = Dispatcher.InvokeAsync(() =>
        {
            ShowShell();
            _shell?.BeginExit();
        }); });
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "ContainerToDrive — drive manager",
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => { _ = Dispatcher.InvokeAsync(ShowShell); };
    }

    private void ShowShell()
    {
        if (_shell is null || IsExiting) return;
        _shell.Show();
        if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal;
        _shell.Activate();
        _shell.Focus();
    }

    internal void NotifyHidden()
    {
        _tray?.ShowBalloonTip(3500, "ContainerToDrive is in the tray",
            "Drives keep running. Open the tray menu to return or explicitly exit.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    internal void ExitInterface()
    {
        IsExiting = true;
        _shell?.StopPolling();
        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never render exception text: transport and parser exceptions can contain credentials.
        e.Handled = true;
        if (_handlingException) return;
        _handlingException = true;
        try
        {
            System.Windows.MessageBox.Show(
                "The interface encountered an unexpected error and will close. " +
                "The controller has not been stopped; drives may still be running. Reopen ContainerToDrive to check their status.",
                "ContainerToDrive", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitInterface();
        }
        finally { _handlingException = false; }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            _ = Dispatcher.InvokeAsync(ApplySystemColors);
    }

    private void ApplySystemColors()
    {
        string[] keys = ["CanvasBrush", "SurfaceBrush", "SidebarBrush", "TextBrush", "MutedBrush",
            "LineBrush", "AccentBrush", "AccentTextBrush", "AccentSoftBrush", "WarningBrush",
            "WarningTextBrush", "DangerBrush", "SuccessBrush"];
        foreach (var key in keys) Resources.Remove(key);
        if (!SystemParameters.HighContrast) return;
        Resources["CanvasBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Resources["SidebarBrush"] = SystemColors.WindowBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedBrush"] = SystemColors.WindowTextBrush;
        Resources["LineBrush"] = SystemColors.WindowTextBrush;
        Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
        Resources["WarningBrush"] = SystemColors.ControlBrush;
        Resources["WarningTextBrush"] = SystemColors.WindowTextBrush;
        Resources["DangerBrush"] = SystemColors.WindowTextBrush;
        Resources["SuccessBrush"] = SystemColors.WindowTextBrush;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _shell?.StopPolling();
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _azureSignIn?.Dispose();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        DispatcherUnhandledException -= OnDispatcherException;
        base.OnExit(e);
    }
}