#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assemblyPath = Join-Path $workspace 'artifacts/bin/ContainerToDrive.Desktop/debug/ContainerToDrive.Desktop.dll'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyPath))
$flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
$theme = [Xml.XmlDocument]::new()
$theme.Load((Join-Path $workspace 'src/ContainerToDrive.Desktop/Themes/Theme.xaml'))
$application = [Windows.Application]::Current
if ($null -eq $application) { $application = [Windows.Application]::new() }
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$application.Resources = [Windows.Markup.XamlReader]::Parse($theme.OuterXml)
$markup = [Xml.XmlDocument]::new()
$markup.Load((Join-Path $workspace 'src/ContainerToDrive.Desktop/ConnectionWindow.xaml'))
$markup.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($element in $markup.SelectNodes('//*')) {
    foreach ($attribute in @('Click', 'Checked', 'Unchecked', 'TextChanged', 'PasswordChanged', 'SelectionChanged', 'KeyUp', 'Loaded', 'Closing', 'Closed')) {
        $element.RemoveAttribute($attribute)
    }
}
$oldContext = [Threading.SynchronizationContext]::Current
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())

function Complete-UiTask([Threading.Tasks.Task] $Task) {
    if (!$Task.IsCompleted) {
        $frame = [Windows.Threading.DispatcherFrame]::new()
        $deadline = [Windows.Threading.DispatcherTimer]::new()
        $deadline.Interval = [TimeSpan]::FromSeconds(5)
        $deadline.Add_Tick({ $frame.Continue = $false }.GetNewClosure())
        $completion = [Action[Threading.Tasks.Task]]{ param($completed) $frame.Continue = $false }.GetNewClosure()
        $null = $Task.ContinueWith($completion, [Threading.Tasks.TaskScheduler]::FromCurrentSynchronizationContext())
        $deadline.Start()
        try { [Windows.Threading.Dispatcher]::PushFrame($frame) } finally { $deadline.Stop() }
        if (!$Task.IsCompleted) { throw 'The bounded UI operation did not complete.' }
    }
    $null = $Task.GetAwaiter().GetResult()
}

function Capture-Visual($Visual, [string] $Name) {
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($Visual.ActualWidth * 1.25), [int][Math]::Ceiling($Visual.ActualHeight * 1.25), 120, 120, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($Visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $directory = Join-Path $workspace 'artifacts/evidence/ux-implementation'
    $null = [IO.Directory]::CreateDirectory($directory)
    $stream = [IO.File]::Create((Join-Path $directory ($Name + '.png')))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

try {
    foreach ($size in @(@{ Name = 'normal'; Width = 800; Height = 660 }, @{ Name = 'minimum'; Width = 520; Height = 460 })) {
        $window = [Windows.Markup.XamlReader]::Parse($markup.OuterXml.Replace('clr-namespace:ContainerToDrive.Desktop', 'clr-namespace:ContainerToDrive.Desktop;assembly=ContainerToDrive.Desktop'))
        $window.Width = $size.Width
        $window.Height = $size.Height
        $window.ShowInTaskbar = $false
        $window.FindName('SasPanel').Visibility = 'Collapsed'
        $window.FindName('EntraPanel').Visibility = 'Visible'
        $window.FindName('EntraMethod').IsChecked = $true
        $window.FindName('AzureAccountText').Text = 'Synthetic test account'
        $window.FindName('StatusText').Text = ''
        $window.FindName('NameBox').Text = 'Project files'
        try {
            $window.Show()
            $window.Content.Background = $window.Background
            $window.UpdateLayout()
            $subscription = $window.FindName('SubscriptionBox')
            if ($subscription.GetType().Assembly.ManifestModule.ModuleVersionId -ne $assembly.ManifestModule.ModuleVersionId) { throw 'The XAML loader resolved a stale desktop assembly. Run this test in a fresh PowerShell terminal after rebuilding.' }
            $setSource = $subscription.GetType().GetMethod('SetSource', $flags)
            $refresh = $subscription.GetType().GetMethod('RefreshAsync', $flags)
            $account = $window.FindName('StorageAccountBox')
            $container = $window.FindName('EntraContainerBox')
            if ($container.IsEnabled) { throw 'Container search must be disabled without an account.' }
            $pending = [Threading.Tasks.TaskCompletionSource[Collections.Generic.IReadOnlyList[object]]]::new()
            $source = [Func[string, Threading.CancellationToken, Threading.Tasks.Task[Collections.Generic.IReadOnlyList[object]]]]{ param($query, $token) return $pending.Task }.GetNewClosure()
            $null = $setSource.Invoke($subscription, @($source))
            $task = $refresh.Invoke($subscription, @($true))
            $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ContextIdle)
            if (!$subscription.IsLoading -or !$subscription.IsDropDownOpen) { throw 'Loading search is not visible in the dropdown.' }
            $popup = $subscription.Template.FindName('PART_Popup', $subscription)
            if (!$popup.IsOpen -or $popup.Child.ActualHeight -lt 35 -or $popup.Child.ActualWidth -lt 100) { throw 'Search popup is blank or incorrectly sized.' }
            Capture-Visual $popup.Child ('connection-search-loading-' + $size.Name)
            $pending.SetResult([object[]]@('Engineering Production', 'Research', 'Development'))
            Complete-UiTask $task
            if ($subscription.Items.Count -ne 3 -or $subscription.IsLoading) { throw 'Search results were not rendered.' }
            $subscription.IsDropDownOpen = $false
            $filtered = [Func[string, Threading.CancellationToken, Threading.Tasks.Task[Collections.Generic.IReadOnlyList[object]]]]{
                param($query, $token)
                $items = [object[]]@(@('Engineering Production', 'Research', 'Development') | Where-Object { $_.Contains($query, [StringComparison]::OrdinalIgnoreCase) })
                return [Threading.Tasks.Task]::FromResult[Collections.Generic.IReadOnlyList[object]]($items)
            }
            $null = $setSource.Invoke($subscription, @($filtered))
            $window.Activate() | Out-Null
            $subscription.Focus() | Out-Null
            $editor = $subscription.Template.FindName('PART_EditableTextBox', $subscription)
            $editor.Focus() | Out-Null
            $editor.Text = 'prod'
            if (!$subscription.IsLoading -or !$subscription.IsDropDownOpen) { throw 'Typing did not trigger dropdown search.' }
            Complete-UiTask ($refresh.Invoke($subscription, @($true)))
            if ($subscription.Items.Count -ne 1 -or $subscription.Items[0] -ne 'Engineering Production' -or $editor.Text -ne 'prod') { throw 'Typed query was not retained or filtered.' }
            $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ContextIdle)
            foreach ($key in @([Windows.Input.Key]::Down, [Windows.Input.Key]::Enter)) {
                $keyEvent = [Windows.Input.KeyEventArgs]::new([Windows.Input.Keyboard]::PrimaryDevice, [Windows.PresentationSource]::FromVisual($editor), [Environment]::TickCount, $key)
                $keyEvent.RoutedEvent = [Windows.Input.Keyboard]::PreviewKeyDownEvent
                $editor.RaiseEvent($keyEvent)
                if (!$keyEvent.Handled) {
                    $keyEvent.RoutedEvent = [Windows.Input.Keyboard]::KeyDownEvent
                    $editor.RaiseEvent($keyEvent)
                }
            }
            $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ContextIdle)
            if ($subscription.SelectedItem -ne 'Engineering Production' -or $editor.Text -ne 'Engineering Production' -or $subscription.IsDropDownOpen) { throw 'Keyboard selection did not commit the result and close the dropdown.' }
            $null = $setSource.Invoke($account, @($filtered))
            $null = $setSource.Invoke($container, @($filtered))
            Complete-UiTask ($refresh.Invoke($container, @($false)))
            $container.SelectedIndex = 0
            $null = $setSource.Invoke($container, @($null))
            if ($container.IsEnabled -or $null -ne $container.SelectedItem -or $container.Text.Length -ne 0 -or $container.Items.Count -ne 0) { throw 'Changing scope retained a stale container selection.' }
            $window.FindName('FormScroll').ScrollToTop()
            $window.UpdateLayout()
            Capture-Visual $window ('connection-dialog-' + $size.Name)
            if ($window.FindName('FormScroll').ViewportHeight -lt 120) { throw 'Dialog leaves too little space for fields.' }
            Write-Output "$($size.Name): compact layout, in-dropdown loading, typed search, keyboard selection, and dependent reset passed."
        }
        finally {
            foreach ($name in @('SubscriptionBox', 'ResourceGroupBox', 'StorageAccountBox', 'EntraContainerBox', 'KeyContainerBox')) { $window.FindName($name).Dispose() }
            $window.Close()
        }
    }
}
finally { [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext) }