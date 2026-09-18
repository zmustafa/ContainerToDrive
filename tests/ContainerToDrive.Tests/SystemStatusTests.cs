using ContainerToDrive.Core;
using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class SystemStatusTests
{
    private static AppSnapshot Ready => new() { EngineAvailable = true, WinFspInstalled = true };

    [Fact]
    public void FrontendRunsWhileUnavailableBackendMakesDependencyStateUnknown()
    {
        var states = SystemStatus.Describe(null, true);
        Assert.Equal("Running", states.Single(state => state.Name == "Frontend").State);
        Assert.Equal("Unavailable", states.Single(state => state.Name == "Backend").State);
        Assert.Equal("Unknown", states.Single(state => state.Name == "Engine").State);
        Assert.Equal("Unknown", states.Single(state => state.Name == "WinFsp").State);
        Assert.Single(states, state => state.Healthy);
    }

    [Fact]
    public void StaleSnapshotDoesNotClaimWorkersAreRunningOrDriverIsInstalled()
    {
        var states = SystemStatus.Describe(Ready with { EngineWorkerCount = 2 }, false);
        Assert.Equal("Unknown", states.Single(state => state.Name == "Engine").State);
        Assert.Equal("Unknown", states.Single(state => state.Name == "WinFsp").State);
        Assert.Single(states, state => state.Healthy);
    }

    [Theory]
    [InlineData(0, "Ready", "Idle; no workers")]
    [InlineData(1, "Running", "1 worker")]
    [InlineData(3, "Running", "3 workers")]
    public void VerifiedEngineDistinguishesIdleFromRunning(int count, string state, string detail)
    {
        var engine = SystemStatus.Describe(Ready with { EngineWorkerCount = count }, true).Single(component => component.Name == "Engine");
        Assert.Equal(state, engine.State);
        Assert.Equal(detail, engine.Detail);
        Assert.True(engine.Healthy);
    }

    [Fact]
    public void FailedBinaryVerificationDoesNotHideExistingWorkers()
    {
        var engine = SystemStatus.Describe(Ready with { EngineAvailable = false, EngineWorkerCount = 1 }, true).Single(component => component.Name == "Engine");
        Assert.Equal("Running", engine.State);
        Assert.False(engine.Healthy);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void InstallIsAvailableOnlyForMissingDependencies(bool engine, bool driver, bool expected) =>
        Assert.Equal(expected, SystemStatus.CanInstall(new() { EngineAvailable = engine, WinFspInstalled = driver }, true, false));

    [Fact]
    public void InstallRequiresCurrentNormalSessionAndNoWorkers()
    {
        var missing = new AppSnapshot();
        Assert.False(SystemStatus.CanInstall(null, true, false));
        Assert.False(SystemStatus.CanInstall(missing, false, false));
        Assert.False(SystemStatus.CanInstall(missing, true, true));
        Assert.False(SystemStatus.CanInstall(missing with { Elevated = true }, true, false));
        Assert.False(SystemStatus.CanInstall(missing with { EngineWorkerCount = 1 }, true, false));
    }

    [Theory]
    [InlineData(MountPhase.Unmounted, true)]
    [InlineData(MountPhase.Faulted, true)]
    [InlineData(MountPhase.Starting, false)]
    [InlineData(MountPhase.Mounted, false)]
    [InlineData(MountPhase.DisconnectRequested, false)]
    public void ActiveAndTransitioningMountsBlockInstallation(MountPhase phase, bool expected)
    {
        var profile = new Profile();
        var snapshot = new AppSnapshot { Profiles = [profile], Mounts = [new() { ProfileId = profile.Id, Phase = phase }] };
        Assert.Equal(expected, SystemStatus.CanInstall(snapshot, true, false));
    }

    [Fact]
    public void MissingMountObservationIsNotIdle()
    {
        var snapshot = new AppSnapshot { Profiles = [new Profile()] };
        Assert.False(SystemStatus.IsIdle(snapshot));
        Assert.False(SystemStatus.CanInstall(snapshot, true, false));
    }
}