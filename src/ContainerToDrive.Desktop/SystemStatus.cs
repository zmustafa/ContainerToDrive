using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

public sealed record ComponentStatus(string Name, string State, string Detail, bool Healthy);

internal static class SystemStatus
{
    internal static IReadOnlyList<ComponentStatus> Describe(AppSnapshot? snapshot, bool current)
    {
        current &= snapshot is not null;
        var workers = current ? snapshot!.EngineWorkerCount : 0;
        return
        [
            new("Frontend", "Running", "Desktop interface", true),
            new("Backend", current ? "Connected" : "Unavailable", "Local controller", current),
            new("Engine", !current ? "Unknown" : workers > 0 ? "Running" : snapshot!.EngineAvailable ? "Ready" : "Unavailable",
                !current ? "Waiting for controller" : workers > 0 ? $"{workers} worker{(workers == 1 ? "" : "s")}" : snapshot!.EngineAvailable ? "Idle; no workers" : "Missing or unverified",
                current && snapshot!.EngineAvailable),
            new("WinFsp", !current ? "Unknown" : snapshot!.WinFspInstalled ? "Installed" : "Missing", "Filesystem driver", current && snapshot!.WinFspInstalled)
        ];
    }

    internal static bool CanInstall(AppSnapshot? snapshot, bool current, bool elevated) =>
        current && !elevated && snapshot is { Elevated: false, EngineWorkerCount: 0 } &&
        (!snapshot.EngineAvailable || !snapshot.WinFspInstalled) && IsIdle(snapshot);

    internal static bool IsIdle(AppSnapshot snapshot) => snapshot.EngineWorkerCount == 0 &&
        !snapshot.Mounts.Any(mount => mount.Phase is MountPhase.Starting or MountPhase.Mounted or MountPhase.DisconnectRequested) &&
        snapshot.Profiles.All(profile => snapshot.Mounts.Any(mount => mount.ProfileId == profile.Id));
}