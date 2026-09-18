using Microsoft.Win32;

namespace ContainerToDrive.Windows;

public sealed class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ContainerToDrive";
    private readonly string _executable;
    private readonly string _command;

    public WindowsStartup(string executable, string dataRoot)
    {
        _command = CreateCommand(executable, dataRoot);
        _executable = AppPaths.NormalizeDataRoot(executable);
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return IsEnabled(key);
    }

    public void SetEnabled(bool enabled)
    {
        if (AppPaths.IsElevated) throw new InvalidOperationException("Change Windows startup from a standard-user session.");
        if (enabled)
        {
            AppPaths.RejectReparsePoints(_executable);
            if (!File.Exists(_executable) || !File.Exists(Path.ChangeExtension(_executable, ".dll")))
                throw new FileNotFoundException("The installed desktop application could not be found.");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            SetEnabled(key, true);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null) SetEnabled(key, false);
        }
    }

    internal bool IsEnabled(RegistryKey? key) => key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value &&
        key.GetValueKind(ValueName) == RegistryValueKind.String && string.Equals(value, _command, StringComparison.OrdinalIgnoreCase);

    internal void SetEnabled(RegistryKey key, bool enabled)
    {
        if (enabled) key.SetValue(ValueName, _command, RegistryValueKind.String);
        else if (IsEnabled(key)) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string CreateCommand(string executable, string dataRoot)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(dataRoot))
            throw new ArgumentException("Windows startup requires absolute executable and data paths.");
        executable = AppPaths.NormalizeDataRoot(executable);
        dataRoot = AppPaths.NormalizeDataRoot(dataRoot);
        if (!string.Equals(Path.GetFileName(executable), "ContainerToDrive.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Windows startup requires the desktop executable.");
        var command = $"\"{executable}\" --minimized --data-root \"{dataRoot}\"";
        if (command.Length > 260) throw new ArgumentException("The executable and data paths are too long for Windows startup.");
        return command;
    }
}