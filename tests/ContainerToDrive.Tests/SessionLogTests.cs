using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class SessionLogTests
{
    [Fact]
    public void NewSessionRecordsFrontendStartup()
    {
        var log = new SessionLog();
        var entry = Assert.Single(log.Entries);
        Assert.Equal("Desktop", entry.Source);
        Assert.Equal("Info", entry.Level);
        Assert.Null(entry.RequestId);
        Assert.True(log.HasEntries);
        Assert.False(log.IsEmpty);
    }

    [Fact]
    public void PollingRecordsOnlyConnectionTransitions()
    {
        var log = new SessionLog();
        for (var index = 0; index < 10; index++) log.RecordResponse("Status", Guid.NewGuid(), true);
        for (var index = 0; index < 10; index++) log.RecordFailure("Status", Guid.NewGuid(), false);
        log.RecordResponse("Status", Guid.NewGuid(), true);
        Assert.Equal(4, log.Entries.Count);
        Assert.Equal("Backend connection established.", log.Entries[0].Message);
        Assert.Equal("Warning", log.Entries[1].Level);
        Assert.All(log.Entries, entry => Assert.Null(entry.RequestId));
    }

    [Theory]
    [InlineData("TestCapabilities", true, "Info", "Test capabilities completed.")]
    [InlineData("TestCapabilities", false, "Error", "Test capabilities failed.")]
    [InlineData("Clone", true, "Info", "Clone connection completed.")]
    [InlineData("Clone", false, "Error", "Clone connection failed.")]
    [InlineData("Validate", true, "Info", "Test connection access completed.")]
    [InlineData("Validate", false, "Error", "Test connection access failed.")]
    public void ActionResultsKeepRequestIdsAndSeverity(string operation, bool success, string level, string message)
    {
        var log = new SessionLog();
        var requestId = Guid.NewGuid();
        log.RecordResponse(operation, requestId, success);
        Assert.Equal(requestId, log.Entries[0].RequestId);
        Assert.Equal(level, log.Entries[0].Level);
        Assert.Equal(message, log.Entries[0].Message);
    }

    [Fact]
    public void UnknownOperationTextIsNeverLogged()
    {
        var log = new SessionLog();
        const string sensitive = "https://synthetic.invalid/container?sig=DO_NOT_LOG";
        log.RecordResponse(sensitive, Guid.NewGuid(), false);
        log.RecordFailure(sensitive, Guid.NewGuid(), false);
        Assert.All(log.Entries, entry => Assert.DoesNotContain(sensitive, entry.Message));
        Assert.StartsWith("Controller request", log.Entries[0].Message);
    }

    [Fact]
    public void CancellationDoesNotClaimTheControllerActionWasStopped()
    {
        var log = new SessionLog();
        var requestId = Guid.NewGuid();
        log.RecordFailure("Mount", requestId, true);
        Assert.Equal("Warning", log.Entries[0].Level);
        Assert.Equal(requestId, log.Entries[0].RequestId);
        Assert.Contains("may still complete", log.Entries[0].Message);
    }

    [Fact]
    public void LogIsBoundedAndNewestEntriesComeFirst()
    {
        var log = new SessionLog();
        var lastId = Guid.Empty;
        for (var index = 0; index < SessionLog.Capacity + 10; index++)
        {
            lastId = Guid.NewGuid();
            log.RecordResponse("Save", lastId, true);
        }
        Assert.Equal(SessionLog.Capacity, log.Entries.Count);
        Assert.Equal(lastId, log.Entries[0].RequestId);
    }

    [Fact]
    public void ClearEmptiesHistoryWithoutChangingConnectionState()
    {
        var log = new SessionLog();
        log.RecordResponse("Status", Guid.NewGuid(), true);
        log.Clear();
        log.RecordResponse("Status", Guid.NewGuid(), true);
        Assert.Empty(log.Entries);
        Assert.True(log.IsEmpty);
        Assert.False(log.HasEntries);
        Assert.Equal("0 session events", log.Summary);
    }
}