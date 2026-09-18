using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;

namespace ContainerToDrive.Desktop;

public sealed record DesktopPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public bool StartMinimized { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool ShowEndpointInfo { get; init; } = true;
    public int DnsRefreshSeconds { get; init; } = 60;
    public int CapabilityCollapseSeconds { get; init; } = 10;
    public int ActivityPeriodDays { get; init; } = 7;
    public decimal DefaultCacheGiB { get; init; } = 10;
    public decimal DefaultMinFreeGiB { get; init; } = 5;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("This settings version is not supported.");
        if (DnsRefreshSeconds is not (30 or 60 or 120 or 300) || CapabilityCollapseSeconds is not (0 or 10 or 30 or 60) ||
            ActivityPeriodDays is not (1 or 7 or 30 or 90 or 365))
            throw new ArgumentException("Select a supported refresh interval and activity period.");
        if (DefaultCacheGiB is < 0.25m or > 1024m) throw new ArgumentException("Cache target must be between 0.25 and 1024 GiB.");
        if (DefaultMinFreeGiB is < 1m or > 1024m) throw new ArgumentException("Free-space reserve must be between 1 and 1024 GiB.");
    }

    public Profile NewProfile(Guid id)
    {
        Validate();
        return new() { Id = id, Revision = 0, ReadOnly = ConnectionFormRules.NewProfileReadOnly, AutoMount = ConnectionFormRules.NewProfileAutoMount,
            CacheMaxBytes = (long)(DefaultCacheGiB * 1024 * 1024 * 1024), MinFreeBytes = (long)(DefaultMinFreeGiB * 1024 * 1024 * 1024) };
    }
}

internal sealed class DesktopPreferencesStore(string dataRoot)
{
    internal const string Name = "desktop-preferences";

    public bool TryLoad(out DesktopPreferences preferences)
    {
        preferences = new();
        byte[]? bytes = null;
        try
        {
            bytes = new ProtectedSettingsStore(dataRoot, createDirectory: false).Read(Name);
            if (bytes is null) return true;
            var loaded = JsonSerializer.Deserialize<DesktopPreferences>(bytes, Wire.Json) ?? throw new InvalidDataException();
            loaded.Validate();
            preferences = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        { return false; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(DesktopPreferences preferences)
    {
        preferences.Validate();
        if (!TryLoad(out _)) throw new InvalidDataException("Existing preferences could not be read and were preserved.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, Wire.Json);
        try { new ProtectedSettingsStore(dataRoot).Write(Name, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed record PreferenceChoice(int Value, string Label);

public sealed class PreferencesEditor : INotifyPropertyChanged
{
    private DesktopPreferences _saved;
    private bool _startMinimized;
    private bool _closeToTray;
    private bool _showEndpointInfo;
    private int _dnsRefreshSeconds;
    private int _capabilityCollapseSeconds;
    private int _activityPeriodDays;
    private string _cacheGiB = "";
    private string _minFreeGiB = "";

    public PreferencesEditor(DesktopPreferences saved) { _saved = saved; Load(saved); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<PreferenceChoice> DnsIntervals { get; } = [new(30, "30 seconds"), new(60, "1 minute"), new(120, "2 minutes"), new(300, "5 minutes")];
    public IReadOnlyList<PreferenceChoice> CollapseIntervals { get; } = [new(0, "Keep open"), new(10, "10 seconds"), new(30, "30 seconds"), new(60, "1 minute")];
    public IReadOnlyList<PreferenceChoice> ActivityPeriods { get; } = [new(1, "Today"), new(7, "7 days"), new(30, "30 days"), new(90, "90 days"), new(365, "1 year")];
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    public bool ShowEndpointInfo { get => _showEndpointInfo; set => Set(ref _showEndpointInfo, value); }
    public int DnsRefreshSeconds { get => _dnsRefreshSeconds; set => Set(ref _dnsRefreshSeconds, value); }
    public int CapabilityCollapseSeconds { get => _capabilityCollapseSeconds; set => Set(ref _capabilityCollapseSeconds, value); }
    public int ActivityPeriodDays { get => _activityPeriodDays; set => Set(ref _activityPeriodDays, value); }
    public string CacheGiB { get => _cacheGiB; set => Set(ref _cacheGiB, value); }
    public string MinFreeGiB { get => _minFreeGiB; set => Set(ref _minFreeGiB, value); }
    public bool HasChanges => !TryBuild(out var value) || value != _saved;
    public string Error
    {
        get { try { Build(); return ""; } catch (ArgumentException exception) { return exception.Message; } }
    }
    public bool HasError => Error.Length != 0;

    public DesktopPreferences Build()
    {
        if (!decimal.TryParse(CacheGiB, NumberStyles.Number, CultureInfo.CurrentCulture, out var cache) ||
            !decimal.TryParse(MinFreeGiB, NumberStyles.Number, CultureInfo.CurrentCulture, out var free))
            throw new ArgumentException("Enter numeric cache and free-space values in GiB.");
        var preferences = new DesktopPreferences
        {
            StartMinimized = StartMinimized, CloseToTray = CloseToTray, ShowEndpointInfo = ShowEndpointInfo,
            DnsRefreshSeconds = DnsRefreshSeconds, CapabilityCollapseSeconds = CapabilityCollapseSeconds,
            ActivityPeriodDays = ActivityPeriodDays, DefaultCacheGiB = cache, DefaultMinFreeGiB = free
        };
        preferences.Validate();
        return preferences;
    }

    public void MarkSaved(DesktopPreferences preferences) { _saved = preferences; Load(preferences); }
    public void RestoreDefaults() => Load(new());

    private bool TryBuild(out DesktopPreferences? value)
    {
        try { value = Build(); return true; }
        catch (ArgumentException) { value = null; return false; }
    }

    private void Load(DesktopPreferences value)
    {
        _startMinimized = value.StartMinimized; _closeToTray = value.CloseToTray; _showEndpointInfo = value.ShowEndpointInfo;
        _dnsRefreshSeconds = value.DnsRefreshSeconds; _capabilityCollapseSeconds = value.CapabilityCollapseSeconds;
        _activityPeriodDays = value.ActivityPeriodDays;
        _cacheGiB = value.DefaultCacheGiB.ToString("0.##", CultureInfo.CurrentCulture);
        _minFreeGiB = value.DefaultMinFreeGiB.ToString("0.##", CultureInfo.CurrentCulture);
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        PropertyChanged?.Invoke(this, new(nameof(HasChanges)));
        PropertyChanged?.Invoke(this, new(nameof(Error)));
        PropertyChanged?.Invoke(this, new(nameof(HasError)));
    }
}