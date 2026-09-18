using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using ContainerToDrive.Core;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ContainerToDrive.Desktop;

public sealed record TransferPeriod(int Days, string Name);
public sealed record TransferConnection(Guid Id, string Name);
public sealed record TransferRow(string Period, string Bytes, string Transfers, string Errors);
public sealed record TransferLegend(string Name, string Value, string Color);

public sealed class TransferDashboard : INotifyPropertyChanged
{
    private TransferPeriod _period;
    private Guid _profileId;
    private TransferReport? _report;
    private bool _loading;
    private bool _failed;
    private bool _online;
    private IReadOnlyList<TransferSummary> _summaries = [];
    public TransferDashboard() => _period = Periods[1];
    public IReadOnlyList<TransferPeriod> Periods { get; } = [new(1, "Today"), new(7, "7 days"), new(30, "30 days"), new(90, "90 days"), new(365, "1 year")];
    public ObservableCollection<TransferConnection> Connections { get; } = [new(Guid.Empty, "All connections")];
    public IReadOnlyList<TransferRow> Rows { get; private set; } = [];
    public IReadOnlyList<TransferLegend> Legend { get; private set; } = [];
    public PlotModel? HistoryPlot { get; private set; }
    public PlotModel? SharePlot { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SelectionChanged;
    public TransferPeriod Period
    {
        get => _period;
        set { if (value is null || value == _period) return; _period = value; ResetSelection(); }
    }
    public Guid ProfileId
    {
        get => _profileId;
        set { if (_profileId == value) return; _profileId = value; ResetSelection(); }
    }
    public bool Loading => _loading;
    public bool HasData => _report?.Buckets.Count > 0;
    public bool NoData => !HasData;
    public bool HasTraffic => _report?.Bytes > 0;
    public bool NoTraffic => !HasTraffic;
    public string PeriodBytes => _report is null ? "--" : FormatBytes(_report.Bytes);
    public string PeriodTransfers => _report?.CompletedTransfers.ToString("N0") ?? "--";
    public string PeriodErrors => _report?.Errors.ToString("N0") ?? "--";
    public string LifetimeBytes => SelectedSummaries.Any(summary => summary.StartedAt is not null) ? FormatBytes(SelectedSummaries.Sum(summary => summary.Bytes)) : "--";
    public string Rate => _online && SelectedSummaries.Any(IsFresh) ? FormatBytes(SelectedSummaries.Where(IsFresh).Sum(summary => summary.BytesPerSecond!.Value)) + "/s" : "--";
    public string Coverage => _loading ? "Loading transfer history..." : _failed ? "History unavailable. Last loaded values retained."
        : _report is null ? "History not loaded." : _report.UnavailableProfiles > 0 ? $"Incomplete history: {_report.UnavailableProfiles} connection(s) unavailable."
        : _report.StartedAt is null ? "No recorded transfers yet."
        : $"Recorded since {_report.StartedAt.Value.ToLocalTime():g} | Last sample {_report.ObservedAt?.ToLocalTime():g}";
    public string EmptyMessage => _loading ? "Loading..." : _failed ? "History unavailable" : "No observations in this period";
    public string ChartTitle => Period.Days == 1 ? "Hourly transfer volume (UTC)" : "Daily transfer volume (UTC)";
    public string ScopeNote => "Engine-reported bytes, including retries; not Azure billing or confirmed delivery. Gaps are unobserved intervals.";
    private IEnumerable<TransferSummary> SelectedSummaries => _summaries.Where(summary => ProfileId == Guid.Empty || summary.ProfileId == ProfileId);
    private static bool IsFresh(TransferSummary summary) => !summary.Unavailable && summary.BytesPerSecond is not null &&
        summary.ObservedAt is { } time && time <= DateTimeOffset.UtcNow.AddSeconds(5) && DateTimeOffset.UtcNow - time < TimeSpan.FromSeconds(45);

    public void UpdateConnections(AppSnapshot snapshot)
    {
        _summaries = snapshot.Transfers;
        _online = true;
        var wanted = snapshot.Profiles.Select(profile => new TransferConnection(profile.Id, DisplayText.Clean(profile.Name))).ToList();
        for (var index = Connections.Count - 1; index > 0; index--)
            if (wanted.All(connection => connection.Id != Connections[index].Id)) Connections.RemoveAt(index);
        foreach (var connection in wanted)
        {
            var index = Connections.ToList().FindIndex(existing => existing.Id == connection.Id);
            if (index < 0) Connections.Add(connection);
            else if (Connections[index] != connection) Connections[index] = connection;
        }
        if (Connections.All(connection => connection.Id != ProfileId)) ProfileId = Guid.Empty;
        Notify();
    }

    public void SetOnline(bool online) { _online = online; Notify(); }
    public void SetLoading() { _loading = _report is null; _failed = false; Notify(); }
    public void SetUnavailable() { _loading = false; _failed = true; Notify(); }
    public void Update(TransferReport report, DateTimeOffset now)
    {
        _report = report;
        _loading = false;
        _failed = false;
        BuildHistory(report, now);
        BuildShares(report);
        Notify();
    }

    private void ResetSelection()
    {
        _report = null; HistoryPlot = null; SharePlot = null; Rows = []; Legend = [];
        _failed = false; _loading = true;
        Notify(); SelectionChanged?.Invoke();
    }

    private void BuildHistory(TransferReport report, DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1 - Period.Days);
        var step = Period.Days == 1 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var end = Period.Days == 1 ? start.AddHours(now.UtcDateTime.Hour) : new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var plot = NewPlot();
        plot.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = Period.Days == 1 ? "HH:mm" : "MMM d",
            Minimum = DateTimeAxis.ToDouble(start.UtcDateTime) - step.TotalDays * 0.25,
            Maximum = DateTimeAxis.ToDouble(end.UtcDateTime) + step.TotalDays * 0.25,
            IntervalType = Period.Days == 1 ? DateTimeIntervalType.Hours : DateTimeIntervalType.Auto, IsPanEnabled = false, IsZoomEnabled = false });
        var max = report.Buckets.Select(bucket => (double)bucket.Bytes).DefaultIfEmpty(0).Max();
        plot.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = Math.Max(1, max * 1.15),
            LabelFormatter = FormatBytes, MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = OxyColor.FromRgb(207, 215, 229),
            IsPanEnabled = false, IsZoomEnabled = false });
        var series = new LineSeries { Color = Palette[0], StrokeThickness = 2, MarkerType = MarkerType.Circle, MarkerSize = 3,
            MarkerFill = Palette[0], TrackerFormatString = "{2:yyyy-MM-dd HH:mm} UTC\n{4:N0} bytes" };
        var rows = new List<TransferRow>();
        var byTime = report.Buckets.ToDictionary(bucket => bucket.Start);
        for (var time = start; time <= end; time += step)
        {
            var label = Period.Days == 1 ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : time.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
            if (byTime.TryGetValue(time, out var bucket))
            {
                series.Points.Add(new(DateTimeAxis.ToDouble(time.UtcDateTime), bucket.Bytes));
                rows.Add(new(label, FormatBytes(bucket.Bytes), bucket.CompletedTransfers.ToString("N0"), bucket.Errors.ToString("N0")));
            }
            else { series.Points.Add(DataPoint.Undefined); rows.Add(new(label, "Not recorded", "--", "--")); }
        }
        plot.Series.Add(series);
        HistoryPlot = plot;
        Rows = rows.AsEnumerable().Reverse().ToList();
    }

    private void BuildShares(TransferReport report)
    {
        var shares = report.Shares.Where(share => share.Bytes > 0).OrderByDescending(share => share.Bytes).ToList();
        var values = shares.Take(7).Select(share => (Name: Connections.FirstOrDefault(connection => connection.Id == share.ProfileId)?.Name ?? "Removed connection", share.Bytes)).ToList();
        if (shares.Count > 7) values.Add(("Other connections", shares.Skip(7).Sum(share => share.Bytes)));
        var plot = NewPlot();
        plot.PlotAreaBorderThickness = new OxyThickness(0);
        var pie = new PieSeries { InnerDiameter = 0.55, StrokeThickness = 1, AngleSpan = 360, StartAngle = -90,
            TickHorizontalLength = 0, TickRadialLength = 0,
            OutsideLabelFormat = "", InsideLabelFormat = "", TrackerFormatString = "{1}\n{2:N0} bytes ({3:0.0}%)" };
        var legend = new List<TransferLegend>();
        for (var index = 0; index < values.Count; index++)
        {
            var color = Palette[index % Palette.Length];
            var value = values[index];
            pie.Slices.Add(new(value.Name, value.Bytes) { Fill = color });
            legend.Add(new(value.Name, $"{FormatBytes(value.Bytes)} | {(double)value.Bytes / report.Bytes:P1}", color.ToString()));
        }
        plot.Series.Add(pie);
        SharePlot = plot;
        Legend = legend;
    }

    private static OxyColor[] Palette => SystemParameters.HighContrast
        ? [ToColor(SystemColors.HighlightColor), ToColor(SystemColors.WindowTextColor), ToColor(SystemColors.GrayTextColor)]
        : [OxyColor.Parse("#087F8C"), OxyColor.Parse("#275BCD"), OxyColor.Parse("#C28314"), OxyColor.Parse("#C64F51"), OxyColor.Parse("#658B36"), OxyColor.Parse("#8B5B9B"), OxyColor.Parse("#52617A"), OxyColor.Parse("#9B623C")];

    private static PlotModel NewPlot() => new()
    {
        DefaultFont = "Segoe UI", DefaultFontSize = 12,
        TextColor = SystemParameters.HighContrast ? ToColor(SystemColors.WindowTextColor) : OxyColor.Parse("#52617A"),
        Background = SystemParameters.HighContrast ? ToColor(SystemColors.WindowColor) : OxyColors.White,
        PlotAreaBorderColor = OxyColors.Transparent, Padding = new OxyThickness(8)
    };
    private static OxyColor ToColor(System.Windows.Media.Color color) => OxyColor.FromArgb(color.A, color.R, color.G, color.B);
    public static string FormatBytes(double value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}