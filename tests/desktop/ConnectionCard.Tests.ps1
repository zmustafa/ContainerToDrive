#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assemblies = @{}
foreach ($name in @('OxyPlot', 'OxyPlot.Wpf.Shared', 'OxyPlot.Wpf')) {
    $path = Join-Path $workspace "artifacts/bin/ContainerToDrive.Desktop/debug/$name.dll"
    $stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($path))
    try { $null = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromStream($stream) }
    finally { $stream.Dispose() }
}
foreach ($name in @('ContainerToDrive.Core', 'ContainerToDrive.Windows', 'ContainerToDrive.Desktop')) {
    $path = Join-Path $workspace "artifacts/bin/$name/debug/$name.dll"
    $stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($path))
    try { $assemblies[$name] = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromStream($stream) }
    finally { $stream.Dispose() }
}
$appType = $assemblies['ContainerToDrive.Desktop'].GetType('ContainerToDrive.Desktop.App')
$application = [Windows.Application]::Current
if ($null -eq $application) {
    $testAssembly = [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly([Reflection.AssemblyName]::new('ContainerToDrive.PresentationTestHost'), [Reflection.Emit.AssemblyBuilderAccess]::Run)
    $testType = $testAssembly.DefineDynamicModule('PresentationTestHost').DefineType('OfflineTrayApp', [Reflection.TypeAttributes]::Public, $appType)
    $startup = $testType.DefineMethod('OnStartup', [Reflection.MethodAttributes]'Family,Virtual,HideBySig', [void], [type[]]@([Windows.StartupEventArgs]))
    $startup.GetILGenerator().Emit([Reflection.Emit.OpCodes]::Ret)
    $testType.DefineMethodOverride($startup, $appType.GetMethod('OnStartup', [Reflection.BindingFlags]'Instance,NonPublic'))
    $application = [Activator]::CreateInstance($testType.CreateType())
}
if (!$appType.IsInstanceOfType($application) -or $application.GetType().Name -ne 'OfflineTrayApp') { throw 'Run this test in a fresh PowerShell terminal so it can create the offline application shell.' }
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$theme = [Xml.XmlDocument]::new()
$theme.Load((Join-Path $workspace 'src/ContainerToDrive.Desktop/Themes/Theme.xaml'))
$application.Resources = [Windows.Markup.XamlReader]::Parse($theme.OuterXml)
$markup = [Xml.XmlDocument]::new()
$markup.Load((Join-Path $workspace 'src/ContainerToDrive.Desktop/MainWindow.xaml'))
$namespaces = [Xml.XmlNamespaceManager]::new($markup.NameTable)
$namespaces.AddNamespace('w', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$templateMarkup = $markup.SelectSingleNode('//w:StackPanel[@x:Name="ConnectionsView"]//w:ItemsControl[@ItemsSource="{Binding Profiles}"]/w:ItemsControl.ItemTemplate/w:DataTemplate', $namespaces)
foreach ($handler in @('OnCloneClick', 'OnValidateClick')) {
    if ($templateMarkup.SelectNodes(".//w:Button[@Click='$handler']", $namespaces).Count -ne 1) { throw "Missing connection card action: $handler" }
}
foreach ($button in $templateMarkup.SelectNodes('.//w:Button', $namespaces)) { $button.RemoveAttribute('Click') }
$templateMarkup.SetAttribute('xmlns:x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$template = [Windows.Markup.XamlReader]::Parse($templateMarkup.OuterXml)

function Find-Buttons([Windows.DependencyObject] $Root) {
    if ($Root -is [Windows.Controls.Button]) { $Root; return }
    for ($index = 0; $index -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Root); $index++) {
        Find-Buttons ([Windows.Media.VisualTreeHelper]::GetChild($Root, $index))
    }
}

function Find-SettingsElements([Windows.DependencyObject] $Root) {
    if ($Root -is [Windows.Controls.Control] -or $Root -is [Windows.Controls.TextBlock]) { $Root }
    for ($index = 0; $index -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Root); $index++) {
        Find-SettingsElements ([Windows.Media.VisualTreeHelper]::GetChild($Root, $index))
    }
}

foreach ($size in @(@{ Name = 'normal'; Width = 850 }, @{ Name = 'minimum'; Width = 480 })) {
    $card = $template.LoadContent()
    $card.DataContext = [pscustomobject]@{
        Name = 'Project files'; Drive = 'P:'; Source = 'synthetic.blob.core.windows.net/documents'
        EndpointInfo = 'Private endpoint (DNS) | 10.24.8.5'
        EndpointVisible = $true
        EndpointDetails = "DNS host: synthetic.blob.core.windows.net`nResolved IPs: 10.24.8.5`nDNS observed: synthetic timestamp"
        EndpointHelp = 'DNS inference only; not verification of the active mount connection or Azure Private Link resource.'
        Authentication = 'Authentication: Microsoft Entra'; Mode = 'WRITABLE'; State = 'Not mounted'
        Uploads = 'Upload status unavailable; previous observations are not current.'
        Observation = 'Last worker observation: Not observed'; Cache = 'Local cache: Not reported / 10 GiB target'
        Expiry = 'Access expires tomorrow'; NeedsRecovery = $false; Recovery = ''; MountLabel = '_Mount'
        RenewLabel = '_Sign in to renew'; CanMount = $true; CanOpen = $false; CanDisconnect = $false
        CanEdit = $true; CanClone = $true; CanValidate = $true; CanRenew = $true; CanRemove = $true
    }
    $window = [Windows.Window]::new()
    $window.Style = $application.Resources['AppWindow']
    $window.WindowStyle = 'None'
    $window.ResizeMode = 'NoResize'
    $window.ShowInTaskbar = $false
    $window.Width = $size.Width + 32
    $window.SizeToContent = 'Height'
    $wrapper = [Windows.Controls.Border]::new()
    $wrapper.Padding = [Windows.Thickness]::new(16)
    $wrapper.Background = $window.Background
    $wrapper.Child = $card
    $window.Content = $wrapper
    try {
        $window.Show()
        $window.UpdateLayout()
        $buttons = @(Find-Buttons $card)
        if ($buttons.Count -ne 8) { throw 'The connection card must retain all eight actions.' }
        $endpointLine = @($card.Child.Children | Where-Object { $_ -is [Windows.Controls.TextBlock] -and $_.Text -eq $card.DataContext.EndpointInfo })
        if ($endpointLine.Count -ne 1 -or !$endpointLine[0].ToolTip) { throw 'The endpoint IP/classification line or its DNS caveat is missing.' }
        $collapsedHeight = $card.ActualHeight
        if ($collapsedHeight -gt 185) { throw 'The default connection card is no longer compact.' }
        $actionRows = @($buttons | ForEach-Object { [Math]::Round($_.TransformToAncestor($card).Transform([Windows.Point]::new(0, 0)).Y) } | Select-Object -Unique)
        if ($actionRows.Count -ne 1) { throw 'The compact card actions should fit on one row at supported widths.' }
        $details = @($card.Child.Children | Where-Object { $_ -is [Windows.Controls.Expander] })[0]
        if ($null -eq $details -or $details.IsExpanded) { throw 'Connection details must initially be collapsed.' }
        $details.IsExpanded = $true
        $window.UpdateLayout()
        if ($card.ActualHeight -lt $collapsedHeight + 60 -or !$details.Content.IsVisible) { throw 'Expanding Details did not reveal the metadata.' }
        $detailText = @($details.Content.Children | ForEach-Object Text)
        foreach ($field in @('Authentication', 'EndpointDetails', 'Observation', 'Cache', 'Expiry')) {
            if ($card.DataContext.$field -notin $detailText) { throw "Connection detail is missing: $field" }
        }
        $details.IsExpanded = $false
        $window.UpdateLayout()
        foreach ($action in @('Clone', 'Test')) {
            $button = @($buttons | Where-Object { [Windows.Automation.AutomationProperties]::GetName($_) -eq "$action connection Project files" })
            if ($button.Count -ne 1 -or !$button[0].IsEnabled -or !$button[0].ToolTip) { throw "$action action is missing, disabled, or lacks its tooltip." }
        }
        foreach ($button in $buttons) {
            $position = $button.TransformToAncestor($window).Transform([Windows.Point]::new(0, 0))
            if ($position.X -lt 0 -or $position.Y -lt 0 -or $position.X + $button.ActualWidth -gt $window.ActualWidth + 0.5 -or $position.Y + $button.ActualHeight -gt $window.ActualHeight + 0.5) { throw 'A connection action is clipped.' }
        }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($window.ActualWidth * 1.25), [int][Math]::Ceiling($window.ActualHeight * 1.25), 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $directory = Join-Path $workspace 'artifacts/evidence/ux-implementation'
        $null = [IO.Directory]::CreateDirectory($directory)
        $stream = [IO.File]::Create((Join-Path $directory ('connection-card-' + $size.Name + '.png')))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $longData = $card.DataContext
        $longData.Name = 'A very long connection name that must not make the compact card grow or overlap its state'
        $longData.Source = 'synthetic.blob.core.windows.net/a-very-long-container-name/a/deeply/nested/prefix'
        $longData.EndpointInfo = 'Mixed endpoints (DNS) | fd12:3456:789a:bcde:1234:5678:90ab:cdef, 2603:1030:1234:5678:1234:5678:90ab:cdef (+4)'
        $card.DataContext = $null
        $card.DataContext = $longData
        $window.UpdateLayout()
        if ([Math]::Abs($card.ActualHeight - $collapsedHeight) -gt 1) { throw 'Long names resized the compact card.' }
        $card.DataContext = [pscustomobject]@{ CanClone = $false; CanValidate = $false }
        $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ContextIdle)
        foreach ($button in $buttons) {
            if ([Windows.Data.BindingOperations]::GetBinding($button, [Windows.UIElement]::IsEnabledProperty).Path.Path -in @('CanClone', 'CanValidate') -and $button.IsEnabled) { throw 'The card action ignored its disabled binding.' }
        }
        Write-Output "$($size.Name): $collapsedHeight DIP card height, one action row, expandable details, and disabled bindings passed."
    }
    finally { $window.Close() }
}

function Find-StatisticsControls([Windows.DependencyObject] $Root) {
    if ($Root -is [OxyPlot.Wpf.PlotView] -or $Root -is [Windows.Controls.ComboBox] -or $Root -is [Windows.Controls.ListBox]) { $Root }
    for ($index = 0; $index -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Root); $index++) {
        Find-StatisticsControls ([Windows.Media.VisualTreeHelper]::GetChild($Root, $index))
    }
}

foreach ($size in @(@{ Name = 'normal'; Width = 850 }, @{ Name = 'minimum'; Width = 480 })) {
    $dashboard = [ContainerToDrive.Desktop.TransferDashboard]::new()
    $snapshot = [ContainerToDrive.Core.AppSnapshot]::new()
    foreach ($name in @('Project files', 'Archive', 'A long connection name with additional context')) {
        $profile = [ContainerToDrive.Core.Profile]::new()
        $profile.Id = [Guid]::NewGuid()
        $profile.Name = $name
        $snapshot.Profiles.Add($profile)
        $summary = [ContainerToDrive.Core.TransferSummary]::new()
        $summary.ProfileId = $profile.Id
        $summary.StartedAt = [DateTimeOffset]::UtcNow.AddDays(-60)
        $summary.ObservedAt = [DateTimeOffset]::UtcNow
        $summary.Bytes = 7GB
        $summary.BytesPerSecond = 256KB
        $snapshot.Transfers.Add($summary)
    }
    $dashboard.UpdateConnections($snapshot)
    $report = [ContainerToDrive.Core.TransferReport]::new()
    $report.StartedAt = [DateTimeOffset]::UtcNow.AddDays(-60)
    $report.ObservedAt = [DateTimeOffset]::UtcNow
    $today = [DateTimeOffset]::new([DateTime]::UtcNow.Date, [TimeSpan]::Zero)
    $volumes = @(200MB, 420MB, 0, 180MB, 650MB, 390MB, 760MB)
    for ($day = 0; $day -lt 7; $day++) {
        if ($day -eq 2) { continue }
        $report.Buckets.Add([ContainerToDrive.Core.TransferBucket]::new($today.AddDays($day - 6), $volumes[$day], 12 + $day, 0, 50))
        $report.Bytes += $volumes[$day]
        $report.CompletedTransfers += 12 + $day
    }
    $report.Shares.Add([ContainerToDrive.Core.TransferShare]::new($snapshot.Profiles[0].Id, [long]($report.Bytes * 0.6)))
    $report.Shares.Add([ContainerToDrive.Core.TransferShare]::new($snapshot.Profiles[1].Id, [long]($report.Bytes * 0.3)))
    $report.Shares.Add([ContainerToDrive.Core.TransferShare]::new($snapshot.Profiles[2].Id, $report.Bytes - $report.Shares[0].Bytes - $report.Shares[1].Bytes))
    $dashboard.Update($report, [DateTimeOffset]::UtcNow)
    $view = [ContainerToDrive.Desktop.TransferDashboardView]::new()
    $view.DataContext = $dashboard
    $window = [Windows.Window]::new()
    $window.Style = $application.Resources['AppWindow']
    $window.WindowStyle = 'None'
    $window.ShowInTaskbar = $false
    $window.Width = $size.Width + 32
    $window.SizeToContent = 'Height'
    $wrapper = [Windows.Controls.Border]::new()
    $wrapper.Padding = [Windows.Thickness]::new(16)
    $wrapper.Child = $view
    $window.Content = $wrapper
    try {
        $window.Show()
        $window.UpdateLayout()
        $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
        $window.UpdateLayout()
        $controls = @(Find-StatisticsControls $view)
        $plots = @($controls | Where-Object { $_ -is [OxyPlot.Wpf.PlotView] })
        if ($plots.Count -ne 2) { throw 'The statistics dashboard must have history and share charts.' }
        foreach ($control in $controls) {
            $position = $control.TransformToAncestor($view).Transform([Windows.Point]::new(0, 0))
            if ($position.X -lt -0.5 -or $position.X + $control.ActualWidth -gt $view.ActualWidth + 0.5) { throw 'A chart or filter extends beyond the dashboard.' }
        }
        foreach ($plot in $plots) {
            if ($plot.ActualModel.PlotArea.Width -lt 80 -or $plot.ActualModel.PlotArea.Height -lt 80) { throw 'A chart did not render a usable plotting area.' }
        }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($window.ActualWidth * 1.25), [int][Math]::Ceiling($window.ActualHeight * 1.25), 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $directory ('transfer-statistics-' + $size.Name + '.png')))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $picker = @($controls | Where-Object { $_ -is [Windows.Controls.ListBox] })[0]
        $picker.SelectedIndex = 2
        if ($dashboard.Period.Days -ne 30 -or $dashboard.HistoryPlot) { throw 'Changing the period did not clear the old report.' }
        $connection = @($controls | Where-Object { $_ -is [Windows.Controls.ComboBox] })[0]
        $connection.SelectedIndex = 1
        if ($dashboard.ProfileId -ne $snapshot.Profiles[0].Id) { throw 'The connection filter binding did not update.' }
        $dashboard.Update([ContainerToDrive.Core.TransferReport]::new(), [DateTimeOffset]::UtcNow)
        $window.UpdateLayout()
        if ($plots[0].IsVisible -or $plots[1].IsVisible -or !$dashboard.NoData) { throw 'Empty history must not show a fabricated chart.' }
        Write-Output "$($size.Name): transfer charts rendered, filters bound, missing intervals retained, empty state passed."
    }
    finally { $window.Close() }
}

$desktopAssembly = $assemblies['ContainerToDrive.Desktop']
$flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
$testRoot = Join-Path $workspace ('.local/capability-presentation-' + [Guid]::NewGuid().ToString('N'))
$client = $assemblies['ContainerToDrive.Windows'].GetType('ContainerToDrive.Windows.ControllerClient').GetConstructor([type[]]@([string])).Invoke([object[]]@([string]$testRoot))
$session = [Activator]::CreateInstance($desktopAssembly.GetType('ContainerToDrive.Desktop.ControllerSession'), [object[]]@($client, $false))
$windowType = $desktopAssembly.GetType('ContainerToDrive.Desktop.MainWindow')
$mainWindow = $windowType.GetConstructors($flags)[0].Invoke([object[]]@($session, [string]$testRoot, $false))
$closing = [Delegate]::CreateDelegate([ComponentModel.CancelEventHandler], $mainWindow, $windowType.GetMethod('OnWindowClosing', $flags))
$mainWindow.ShowInTaskbar = $false
$showResults = $windowType.GetMethod('ShowCapabilities', $flags)
$collapseTimer = $windowType.GetField('_capabilityCollapseTimer', $flags).GetValue($mainWindow)
$capabilityResults = $mainWindow.FindName('CapabilityResults')
$response = [Activator]::CreateInstance($assemblies['ContainerToDrive.Core'].GetType('ContainerToDrive.Core.Response'))
$response.Success = $true
$response.Capabilities = [Collections.Generic.List[ContainerToDrive.Core.CapabilityCheck]]::new()
$response.Capabilities.Add([ContainerToDrive.Core.CapabilityCheck]::new('Backend', $true, 'Synthetic backend result.'))
try {
    $mainWindow.Show()
    $mainWindow.UpdateLayout()
    $browse = $mainWindow.FindName('BrowseDataRootButton')
    if ($null -eq $browse -or [Windows.Automation.AutomationProperties]::GetName($browse) -ne 'Browse for application data folder') { throw 'The data folder picker button is missing.' }
    if ($browse.IsEnabled) { throw 'Changing the data folder must be disabled without controller status.' }
    $locationSnapshot = [ContainerToDrive.Core.AppSnapshot]::new()
    $locationProfile = [ContainerToDrive.Core.Profile]::new()
    $locationSnapshot.Profiles.Add($locationProfile)
    $locationMount = [ContainerToDrive.Core.MountStatus]::new()
    $locationMount.ProfileId = $locationProfile.Id
    $locationMount.Phase = [ContainerToDrive.Core.MountPhase]::Unmounted
    $locationSnapshot.Mounts.Add($locationMount)
    $onSnapshot = $windowType.GetMethod('OnSnapshot', $flags)
    $layoutSnapshot = [ContainerToDrive.Core.AppSnapshot]::new()
    $layoutSnapshot.EngineAvailable = $true
    $layoutSnapshot.WinFspInstalled = $true
    $layoutSnapshot.WritableEnabled = $true
    $connectionNames = @('Project files', 'Archive', 'Team uploads', 'Reports', 'A long connection name that stays within the final card')
    for ($index = 0; $index -lt $connectionNames.Count; $index++) {
        $profile = [ContainerToDrive.Core.Profile]::new()
        $profile.Name = $connectionNames[$index]
        $profile.DriveLetter = [string][char](80 + $index)
        $profile.Endpoint = 'https://synthetic.blob.core.windows.net'
        $profile.Container = 'documents'
        $layoutSnapshot.Profiles.Add($profile)
        $mount = [ContainerToDrive.Core.MountStatus]::new()
        $mount.ProfileId = $profile.Id
        $mount.Phase = [ContainerToDrive.Core.MountPhase]::Unmounted
        $layoutSnapshot.Mounts.Add($mount)
    }
    $null = $onSnapshot.Invoke($mainWindow, @($layoutSnapshot))
    $connectionList = $mainWindow.FindName('ConnectionCards')
    if ($null -eq $connectionList) { throw 'The responsive connection list is missing.' }
    $mainWindow.Height = 900
    foreach ($size in @(@{ Name = 'wide'; Width = 1400; Columns = 2 }, @{ Name = 'two-column-minimum'; Width = 1260; Columns = 2 }, @{ Name = 'below-breakpoint'; Width = 1220; Columns = 1 }, @{ Name = 'normal'; Width = 940; Columns = 1 }, @{ Name = 'minimum'; Width = 760; Columns = 1 }, @{ Name = 'wide-again'; Width = 1500; Columns = 2 })) {
        $mainWindow.Width = $size.Width
        $mainWindow.UpdateLayout()
        $mainWindow.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
        $mainWindow.UpdateLayout()
        $layoutCards = @(for ($index = 0; $index -lt $connectionList.Items.Count; $index++) {
            $presenter = $connectionList.ItemContainerGenerator.ContainerFromIndex($index)
            [Windows.Media.VisualTreeHelper]::GetChild($presenter, 0)
        })
        if ($layoutCards.Count -ne 5) { throw 'The connection grid lost a mount.' }
        $expectedWidth = $connectionList.ActualWidth / $size.Columns - 12
        for ($index = 0; $index -lt $layoutCards.Count; $index++) {
            $card = $layoutCards[$index]
            $position = $card.TransformToAncestor($connectionList).Transform([Windows.Point]::new(0, 0))
            $expectedLeft = 6 + ($index % $size.Columns) * ($expectedWidth + 12)
            if ([Math]::Abs($card.ActualWidth - $expectedWidth) -gt 1 -or [Math]::Abs($position.X - $expectedLeft) -gt 1) { throw "$($size.Name): cards did not form $($size.Columns) equal-width columns." }
            $rowStart = $layoutCards[$index - ($index % $size.Columns)].TransformToAncestor($connectionList).Transform([Windows.Point]::new(0, 0))
            if ([Math]::Abs($position.Y - $rowStart.Y) -gt 0.5) { throw 'Cards in the same row are not top-aligned.' }
            if ($index -ge $size.Columns) {
                $previous = $layoutCards[$index - $size.Columns]
                $previousTop = $previous.TransformToAncestor($connectionList).Transform([Windows.Point]::new(0, 0)).Y
                if ($position.Y -lt $previousTop + $previous.ActualHeight + 9.5) { throw 'Connection rows overlap or lost their spacing.' }
            }
            $buttons = @(Find-Buttons $card)
            if ($buttons.Count -ne 8) { throw 'A responsive card lost an action.' }
            foreach ($button in $buttons) {
                $buttonPosition = $button.TransformToAncestor($card).Transform([Windows.Point]::new(0, 0))
                if ($buttonPosition.X -lt -0.5 -or $buttonPosition.X + $button.ActualWidth -gt $card.ActualWidth + 0.5 -or $buttonPosition.Y -lt -0.5 -or $buttonPosition.Y + $button.ActualHeight -gt $card.ActualHeight + 0.5) { throw 'A responsive card action is clipped.' }
            }
        }
        if ($size.Name -in @('wide', 'minimum')) {
            $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($mainWindow.ActualWidth * 1.25), [int][Math]::Ceiling($mainWindow.ActualHeight * 1.25), 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
            $bitmap.Render($mainWindow)
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $stream = [IO.File]::Create((Join-Path $directory ('connection-grid-' + $size.Name + '.png')))
            try { $encoder.Save($stream) } finally { $stream.Dispose() }
        }
        $collapsedHeight = $layoutCards[0].ActualHeight
        $lastHeight = $layoutCards[-1].ActualHeight
        $details = @($layoutCards[0].Child.Children | Where-Object { $_ -is [Windows.Controls.Expander] })[0]
        $details.IsExpanded = $true
        $mainWindow.UpdateLayout()
        $expandedTop = $layoutCards[0].TransformToAncestor($connectionList).Transform([Windows.Point]::new(0, 0)).Y
        $nextRowTop = $layoutCards[$size.Columns].TransformToAncestor($connectionList).Transform([Windows.Point]::new(0, 0)).Y
        if ($layoutCards[0].ActualHeight -lt $collapsedHeight + 60 -or $nextRowTop -lt $expandedTop + $layoutCards[0].ActualHeight + 9.5) { throw 'Expanded details overlap the next connection row.' }
        if ([Math]::Abs($layoutCards[-1].ActualHeight - $lastHeight) -gt 1) { throw 'Expanding one card stretched cards in other rows.' }
        $details.IsExpanded = $false
        $mainWindow.UpdateLayout()
        Write-Output "$($size.Name): five mounts in $($size.Columns) fluid columns, row spacing, all actions, and expanded details passed."
    }
    $mainWindow.Width = 940
    $mainWindow.Height = 680
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    if (!$browse.IsEnabled) { throw 'The data folder picker should be enabled for confirmed idle state.' }
    $locationMount.Phase = [ContainerToDrive.Core.MountPhase]::Mounted
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    if ($browse.IsEnabled) { throw 'The data folder picker must be disabled with a mounted drive.' }
    $locationMount.Phase = [ContainerToDrive.Core.MountPhase]::Unmounted
    $locationMount.RecoveryRequired = $true
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    if ($browse.IsEnabled) { throw 'The data folder picker must be disabled during recovery.' }
    $locationMount.RecoveryRequired = $false
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    if (!$browse.ToolTip) { throw 'The data folder picker is missing its tooltip.' }
    Write-Output 'Data location: accessible browse action and unavailable/mounted/recovery/idle guards passed without changing folders.'
    $startupCheck = $mainWindow.FindName('StartWithWindowsCheck')
    if ($null -eq $startupCheck -or [Windows.Automation.AutomationProperties]::GetName($startupCheck) -ne 'Start with Windows') { throw 'The Windows startup setting is missing.' }
    if ([Windows.Data.BindingOperations]::GetBinding($startupCheck, [Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).Path.Path -ne 'StartWithWindows') { throw 'The Windows startup setting is not bound to persisted state.' }
    $appType.GetField('_shell', $flags).SetValue($application, $mainWindow)
    $windowType.GetField('_hiddenNoticeShown', $flags).SetValue($mainWindow, $true)
    $null = $appType.GetMethod('CreateTray', $flags).Invoke($application, @())
    $tray = $appType.GetField('_tray', $flags).GetValue($application)
    $mainWindow.Close()
    if ($mainWindow.IsVisible -or !$mainWindow.IsLoaded -or !$tray.Visible) { throw 'Closing the app did not preserve the loaded window and tray icon.' }
    $null = $appType.GetMethod('ShowShell', $flags).Invoke($application, @())
    if (!$mainWindow.IsVisible -or $mainWindow.WindowState -eq 'Minimized') { throw 'The tray action did not restore the app.' }
    Write-Output 'Startup/tray: setting binding, close-to-tray, and tray reopening passed without controller access.'
    $null = $showResults.Invoke($mainWindow, @($response))
    $mainWindow.UpdateLayout()
    if (!$capabilityResults.IsExpanded -or !$collapseTimer.IsEnabled -or $collapseTimer.Interval.TotalSeconds -ne 10) { throw 'New capability results must expand with a ten-second timer.' }
    $summary = $mainWindow.CapabilitySummary
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $onCollapsed = [Windows.RoutedEventHandler]{ param($eventSource, $routedEvent) $frame.Continue = $false }.GetNewClosure()
    $capabilityResults.Add_Collapsed($onCollapsed)
    $deadline = [Windows.Threading.DispatcherTimer]::new()
    $deadline.Interval = [TimeSpan]::FromSeconds(15)
    $deadline.Add_Tick({ $frame.Continue = $false }.GetNewClosure())
    $deadline.Start()
    try { [Windows.Threading.Dispatcher]::PushFrame($frame) }
    finally { $deadline.Stop(); $capabilityResults.Remove_Collapsed($onCollapsed) }
    if ($capabilityResults.IsExpanded -or $collapseTimer.IsEnabled -or $elapsed.Elapsed.TotalSeconds -lt 9.5) { throw 'Capability results did not auto-collapse after ten seconds.' }
    $capabilityResults.IsExpanded = $true
    if ($collapseTimer.IsEnabled -or $mainWindow.CapabilitySummary -ne $summary -or $mainWindow.Capabilities.Count -ne 2) { throw 'Reopening lost the last results or started another timer.' }
    $null = $showResults.Invoke($mainWindow, @($response))
    if (!$capabilityResults.IsExpanded -or !$collapseTimer.IsEnabled) { throw 'A repeated test did not restart the collapse timer.' }
    $capabilityResults.IsExpanded = $false
    if ($collapseTimer.IsEnabled) { throw 'Manual collapse did not cancel the timer.' }
    $capabilityResults.IsExpanded = $true
    if ($collapseTimer.IsEnabled) { throw 'Manual re-expansion restarted auto-collapse.' }
    $null = $showResults.Invoke($mainWindow, @($null))
    if (!$capabilityResults.IsExpanded -or !$collapseTimer.IsEnabled -or $mainWindow.Capabilities[1].Passed) { throw 'Failed capability results did not use the same disclosure behavior.' }
    Write-Output 'Capabilities: real ten-second auto-collapse, retained results, manual reopening, repeated tests, and failure state passed.'
    $capabilityResults.IsExpanded = $false
    if (Test-Path -LiteralPath $testRoot) { throw 'Reading default preferences unexpectedly created application data.' }
    $locationProfile.Name = 'Project files'
    $locationProfile.Endpoint = 'https://synthetic.blob.core.windows.net'
    $locationProfile.Container = 'documents'
    $locationSnapshot.EngineAvailable = $true
    $locationSnapshot.WinFspInstalled = $true
    $locationSnapshot.WritableEnabled = $true
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    $settings = $mainWindow.FindName('SettingsView')
    $mainWindow.FindName('SettingsNav').IsChecked = $true
    $mainWindow.UpdateLayout()
    if (!$settings.IsVisible -or $mainWindow.FindName('ConnectionsView').IsVisible -or $mainWindow.ShowAddConnection -or $mainWindow.ViewTitle -ne 'Settings') { throw 'Settings navigation did not switch pages and header actions.' }
    foreach ($controlName in @('StartWithWindowsCheck', 'DataRootBox', 'BrowseDataRootButton', 'DefaultCacheBox', 'DefaultMinFreeBox', 'DnsIntervalBox', 'CapabilityCollapseBox', 'ActivityPeriodBox')) {
        $control = $mainWindow.FindName($controlName)
        if ($null -eq $control -or !$control.IsDescendantOf($settings)) { throw "Setting is not on the Settings page: $controlName" }
    }
    $configure = @(Find-Buttons $settings | Where-Object { [Windows.Automation.AutomationProperties]::GetName($_) -eq 'Configure Project files' })[0]
    if (!$configure.IsEnabled) { throw 'Confirmed idle connections should be configurable from Settings.' }
    $locationMount.Phase = [ContainerToDrive.Core.MountPhase]::Mounted
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    if ($configure.IsEnabled -or $browse.IsEnabled) { throw 'Settings bypassed mounted-drive guards.' }
    $locationMount.Phase = [ContainerToDrive.Core.MountPhase]::Unmounted
    $null = $onSnapshot.Invoke($mainWindow, @($locationSnapshot))
    $install = @(Find-Buttons $settings | Where-Object { [Windows.Automation.AutomationProperties]::GetName($_) -eq 'Install dependencies' })
    if ($install.Count -ne 1 -or $install[0].IsEnabled) { throw 'Dependency setup is missing from Settings or enabled without missing dependencies.' }
    $scroll = $mainWindow.FindName('PageScroll')
    foreach ($size in @(@{ Name = 'normal'; Width = 940; Height = 680 }, @{ Name = 'minimum'; Width = 760; Height = 520 })) {
        $mainWindow.Width = $size.Width
        $mainWindow.Height = $size.Height
        $scroll.ScrollToTop()
        $mainWindow.UpdateLayout()
        foreach ($element in @(Find-SettingsElements $settings)) {
            if (!$element.IsVisible -or $element.ActualWidth -eq 0) { continue }
            $position = $element.TransformToAncestor($settings).Transform([Windows.Point]::new(0, 0))
            if ($position.X -lt -0.5 -or $position.X + $element.ActualWidth -gt $settings.ActualWidth + 0.5) { throw "Settings element exceeds the page width: $($element.GetType().Name) $($element.Name)" }
        }
        $capture = 0
        $pageHeight = $scroll.ViewportHeight
        for ($offset = 0; $offset -lt $scroll.ExtentHeight; $offset += $pageHeight) {
            $scroll.ScrollToVerticalOffset($offset)
            $mainWindow.UpdateLayout()
            $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($mainWindow.ActualWidth * 1.25), [int][Math]::Ceiling($mainWindow.ActualHeight * 1.25), 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
            $bitmap.Render($mainWindow)
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $stream = [IO.File]::Create((Join-Path $directory ("settings-" + $size.Name + "-$capture.png")))
            try { $encoder.Save($stream) } finally { $stream.Dispose() }
            $capture++
        }
        Write-Output "$($size.Name): Settings sections fit the page width; $capture native screenshots captured."
    }
    $scroll.ScrollToTop()
    $mainWindow.UpdateLayout()
    $save = $mainWindow.FindName('SavePreferencesButton')
    $cache = $mainWindow.FindName('DefaultCacheBox')
    if ($save.IsEnabled) { throw 'Unchanged preferences should not be saved again.' }
    $cache.Text = 'invalid'
    if (!$mainWindow.Preferences.HasError -or $save.IsEnabled) { throw 'Invalid cache text did not block saving.' }
    $cache.Text = '22'
    $mainWindow.FindName('DefaultMinFreeBox').Text = '6'
    $mainWindow.FindName('StartMinimizedCheck').IsChecked = $true
    $mainWindow.FindName('CloseToTrayCheck').IsChecked = $false
    $mainWindow.FindName('EndpointInfoCheck').IsChecked = $false
    $mainWindow.FindName('CapabilityCollapseBox').SelectedValue = 0
    $mainWindow.FindName('ActivityPeriodBox').SelectedValue = 30
    $mainWindow.FindName('DnsIntervalBox').SelectedValue = 300
    if (!$save.IsEnabled -or $mainWindow.FindName('DnsIntervalBox').IsEnabled -or $mainWindow.Statistics.Period.Days -ne 7 -or !$mainWindow.Profiles[0].EndpointVisible) { throw 'Draft preferences applied early or have incorrect bindings.' }
    $save.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    if ($save.IsEnabled -or $mainWindow.Preferences.HasChanges -or $mainWindow.Statistics.Period.Days -ne 30 -or $mainWindow.Profiles[0].EndpointVisible -or !$mainWindow.ContactText.Contains('exit confirmation')) { throw 'Saving preferences failed to apply their behavior.' }
    if ($locationProfile.CacheMaxBytes -ne 10GB -or $locationProfile.MinFreeBytes -ne 5GB) { throw 'Saving defaults modified an existing connection.' }
    $null = $showResults.Invoke($mainWindow, @($response))
    if (!$capabilityResults.IsExpanded -or $collapseTimer.IsEnabled) { throw 'Keep-open capability preference was ignored.' }
    $windowType.GetField('_busy', $flags).SetValue($mainWindow, $true)
    $closeAttempt = [ComponentModel.CancelEventArgs]::new()
    $null = $windowType.GetMethod('OnWindowClosing', $flags).Invoke($mainWindow, @($mainWindow, $closeAttempt))
    if (!$closeAttempt.Cancel -or !$mainWindow.IsVisible) { throw 'Close-to-exit did not respect the existing busy guard.' }
    $windowType.GetField('_busy', $flags).SetValue($mainWindow, $false)
    $reopened = $windowType.GetConstructors($flags)[0].Invoke([object[]]@($session, [string]$testRoot, $false))
    try {
        if ($reopened.Preferences.CacheGiB -ne '22' -or $reopened.Preferences.MinFreeGiB -ne '6' -or $reopened.Preferences.CloseToTray -or $reopened.Preferences.ShowEndpointInfo -or $reopened.Statistics.Period.Days -ne 30 -or !$windowType.GetProperty('StartMinimized', $flags).GetValue($reopened)) { throw 'Reopened window did not restore protected preferences.' }
    }
    finally {
        $reopened.Remove_Closing([Delegate]::CreateDelegate([ComponentModel.CancelEventHandler], $reopened, $windowType.GetMethod('OnWindowClosing', $flags)))
        $reopened.Close()
    }
    $mainWindow.FindName('ConnectionsNav').IsChecked = $true
    if ($settings.IsVisible -or !$mainWindow.ShowAddConnection) { throw 'Returning from Settings did not restore connection actions.' }
    if (@(Get-ChildItem -LiteralPath $testRoot -File -Recurse).Count -ne 1) { throw 'The Settings check created unexpected application files.' }
    Write-Output 'Settings: navigation, idle guards, draft validation, save/reopen, DNS visibility, activity defaults, keep-open results, and busy-close behavior passed without controller access.'
}
finally {
    $ownedTray = $appType.GetField('_tray', $flags).GetValue($application)
    if ($null -ne $ownedTray) { $ownedTray.Visible = $false; $ownedTray.Dispose() }
    $ownedMenu = $appType.GetField('_trayMenu', $flags).GetValue($application)
    if ($null -ne $ownedMenu) { $ownedMenu.Dispose() }
    $appType.GetField('_shell', $flags).SetValue($application, $null)
    $mainWindow.Remove_Closing($closing)
    $mainWindow.Close()
    if (Test-Path -LiteralPath $testRoot) { [IO.Directory]::Delete($testRoot, $true) }
}
if ($collapseTimer.IsEnabled) { throw 'Closing the window left the capability timer running.' }
if (Test-Path -LiteralPath $testRoot) { throw 'The presentation check unexpectedly created application data.' }