using ContainerToDrive.Core;
using ContainerToDrive.Desktop;
using OxyPlot.Series;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class TransferStatisticsTests
{
    [Fact]
    public void DashboardPlotsMeasuredValuesAndGapsWithoutFabricatingDirectionOrTraffic()
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
        var dashboard = new TransferDashboard();
        dashboard.UpdateConnections(new AppSnapshot { Profiles = [new() { Id = profileId, Name = "Project files" }] });
        dashboard.Update(new TransferReport
        {
            Bytes = 3072, CompletedTransfers = 3,
            Buckets = [new(new DateTimeOffset(now.UtcDateTime.Date.AddDays(-2), TimeSpan.Zero), 1024, 1, 0, 1), new(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), 2048, 2, 0, 1)],
            Shares = [new(profileId, 3072)], StartedAt = now.AddDays(-2), ObservedAt = now
        }, now);
        var line = Assert.IsType<LineSeries>(Assert.Single(dashboard.HistoryPlot!.Series));
        Assert.Equal(7, line.Points.Count);
        Assert.Equal(5, line.Points.Count(point => double.IsNaN(point.Y)));
        Assert.Equal(new double[] { 1024, 2048 }, line.Points.Where(point => !double.IsNaN(point.Y)).Select(point => point.Y));
        Assert.Equal(7, dashboard.Rows.Count);
        Assert.Equal(5, dashboard.Rows.Count(row => row.Bytes == "Not recorded"));
        Assert.Equal(3072, Assert.Single(Assert.IsType<PieSeries>(Assert.Single(dashboard.SharePlot!.Series)).Slices).Value);
        Assert.Equal("Project files", Assert.Single(dashboard.Legend).Name);
        dashboard.Period = dashboard.Periods[2];
        Assert.Null(dashboard.HistoryPlot);
        Assert.True(dashboard.Loading);
        Assert.Empty(dashboard.Rows);
        dashboard.Update(new TransferReport(), now);
        Assert.False(dashboard.HasTraffic);
        Assert.True(dashboard.NoData);
        Assert.Empty(Assert.IsType<PieSeries>(Assert.Single(dashboard.SharePlot!.Series)).Slices);
    }

    [Fact]
    public void DashboardScopesLifetimeTotalsAndRejectsStaleRates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var dashboard = new TransferDashboard();
        dashboard.UpdateConnections(new AppSnapshot
        {
            Profiles = [new() { Id = first, Name = "First" }, new() { Id = second, Name = "Second" }],
            Transfers = [new() { ProfileId = first, StartedAt = now, ObservedAt = now, Bytes = 1024, BytesPerSecond = 10 },
                new() { ProfileId = second, StartedAt = now, ObservedAt = now.AddMinutes(-2), Bytes = 2048, BytesPerSecond = 20 }]
        });
        Assert.Equal(TransferDashboard.FormatBytes(3072), dashboard.LifetimeBytes);
        Assert.Equal("10 B/s", dashboard.Rate);
        dashboard.SetOnline(false);
        Assert.Equal("--", dashboard.Rate);
        dashboard.SetOnline(true);
        dashboard.ProfileId = second;
        Assert.Equal(TransferDashboard.FormatBytes(2048), dashboard.LifetimeBytes);
        Assert.Equal("--", dashboard.Rate);
        dashboard.UpdateConnections(new AppSnapshot());
        Assert.Equal(Guid.Empty, dashboard.ProfileId);
        Assert.Single(dashboard.Connections);
    }

    [Fact]
    public void RepeatedSamplesAndRemountsDoNotDoubleCount()
    {
        var time = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var session = Guid.NewGuid();
        var history = new TransferHistory().Record(session, new(100, 2, 1, 10), time);
        history = history.Record(session, new(100, 2, 1, 0), time.AddSeconds(15));
        history = history.Record(session, new(140, 3, 1, 5), time.AddSeconds(30));
        history = history.Record(Guid.NewGuid(), new(20, 1, 0, 2), time.AddSeconds(45));
        Assert.Equal(160, history.Bytes);
        Assert.Equal(4, history.CompletedTransfers);
        Assert.Equal(1, history.Errors);
        Assert.Equal(new TransferBucket(new DateTimeOffset(time.UtcDateTime.Date, TimeSpan.Zero), 160, 4, 1, 4), Assert.Single(history.Days));
        history.Validate();
    }

    [Fact]
    public void CounterResetsAndUtcBoundariesAreRecordedWithoutNegativeDeltas()
    {
        var session = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-09-10T23:59:59Z");
        var history = new TransferHistory().Record(session, new(100, 2, 3, 10), time);
        history = history.Record(session, new(20, 1, 0, 2), time.AddSeconds(15).ToOffset(TimeSpan.FromHours(5)));
        Assert.Equal(120, history.Bytes);
        Assert.Equal(3, history.CompletedTransfers);
        Assert.Equal(3, history.Errors);
        Assert.Equal(2, history.Days.Count);
        Assert.Equal(20, history.Days[1].Bytes);
        Assert.Equal(TimeSpan.Zero, history.Days[1].Start.Offset);
        Assert.Same(history, history.Record(session, new(200, 4, 4, 10), time));
    }

    [Fact]
    public void RetentionIsBoundedWhileLifetimeTotalsSurvive()
    {
        var session = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var history = new TransferHistory();
        for (var day = 0; day < 400; day++)
            history = history.Record(session, new((day + 1) * 10, day + 1, 0, 0), time.AddDays(day));
        Assert.Equal(4000, history.Bytes);
        Assert.Equal(366, history.Days.Count);
        Assert.Equal(2, history.Hours.Count);
        Assert.Equal(time, history.StartedAt);
        history.Validate();
    }

    [Fact]
    public void MissingPeriodsAreNotInventedAndSerializationPreservesTheBaseline()
    {
        var session = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        var history = new TransferHistory().Record(session, new(100, 1, 0, 10), time);
        var json = System.Text.Json.JsonSerializer.Serialize(history, Wire.Json);
        history = System.Text.Json.JsonSerializer.Deserialize<TransferHistory>(json, Wire.Json)!;
        history = history.Record(session, new(200, 2, 0, 10), time.AddDays(7));
        Assert.Equal(200, history.Bytes);
        Assert.Equal(2, history.Days.Count);
        Assert.Throws<ArgumentException>(() => history.Record(session, new(-1, 0, 0, 0), time.AddDays(8)));
    }
}