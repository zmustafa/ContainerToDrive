using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Rclone;
using ContainerToDrive.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class ControllerTests
{
    [Fact]
    public async Task DataLocationChangeRejectsUnconfirmedRecoveryAndLockedCacheWithoutChangingTheSource()
    {
        var credential = SyntheticCredential.Create();
        await WithControllerAsync([credential], async (running, token) =>
        {
            var destination = running.Root.Root + "-blocked-copy";
            var profile = Assert.Single(Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Save", Profile = SyntheticCredential.Profile(), Credential = CredentialSubmission.ContainerSas(credential.Url)
            }, token)).Profiles);
            var store = running.Root.OpenStore();
            try
            {
                Assert.False((await running.SendAsync(new Request { Operation = "RelocateData", DestinationDataRoot = destination }, token)).Success);
                Assert.False(Directory.Exists(destination));
                store.WriteIntent(new() { ProfileId = profile.Id, Identity = Validation.Identity(profile), EngineVersion = EngineLocator.Version, Uncertain = true });
                Assert.False((await running.SendAsync(new Request { Operation = "RelocateData", DestinationDataRoot = destination, Confirm = true }, token)).Success);
                await Assert.ThrowsAsync<InvalidOperationException>(() => DataRelocation.CopyAsync(store, destination, token));
                Assert.False(Directory.Exists(destination));
                store.WriteIntent(new() { ProfileId = profile.Id, Identity = Validation.Identity(profile), EngineVersion = EngineLocator.Version, Uncertain = false });
                using (var owner = new FileStream(Path.Combine(store.CachePath(profile.Id), ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    await Assert.ThrowsAnyAsync<IOException>(() => DataRelocation.CopyAsync(store, destination, token));
                Assert.Equal(store.DataRoot, DataLocation.Resolve(store.DataRoot));
                Assert.Equal(profile, Assert.Single(store.List()));
                Assert.Equal(credential.Url, store.ReadSecret(profile.Id));
                Assert.True((await running.SendAsync(new Request { Operation = "Status" }, token)).Success);
            }
            finally { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
        });
    }

    [Fact]
    public async Task DataFolderCopyPreservesProfilesCredentialsCacheAndProtectedHistoryWithoutSwitchingLocations()
    {
        var credential = SyntheticCredential.Create();
        await WithControllerAsync([credential], async (running, token) =>
        {
            var profile = Assert.Single(Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Save", Profile = SyntheticCredential.Profile(), Credential = CredentialSubmission.ContainerSas(credential.Url)
            }, token)).Profiles);
            var store = running.Root.OpenStore();
            var settings = new ProtectedSettingsStore(store.DataRoot);
            settings.Write("synthetic-setting", Encoding.UTF8.GetBytes("synthetic protected value"));
            var history = new TransferHistoryStore(store.DataRoot);
            history.Record(profile.Id, Guid.NewGuid(), new(1234, 3, 1, 0), DateTimeOffset.UtcNow);
            var cacheFile = Path.Combine(store.CachePath(profile.Id), "synthetic-data.bin");
            File.WriteAllBytes(cacheFile, [5, 10, 15]);
            var destination = store.DataRoot + "-relocated";
            try
            {
                Assert.Equal(destination, await DataRelocation.CopyAsync(store, destination, token));
                var copied = new ProfileStore(destination);
                Assert.Equal(profile, Assert.Single(copied.List()));
                Assert.Equal(store.ReadCredential(profile.Id), copied.ReadCredential(profile.Id));
                Assert.Equal(new byte[] { 5, 10, 15 }, File.ReadAllBytes(Path.Combine(copied.CachePath(profile.Id), "synthetic-data.bin")));
                Assert.Equal(settings.Read("synthetic-setting"), new ProtectedSettingsStore(destination).Read("synthetic-setting"));
                Assert.Equal(1234, new TransferHistoryStore(destination).Summary(profile.Id, false).Bytes);
                Assert.Equal(store.DataRoot, DataLocation.Resolve(store.DataRoot));
                Assert.True(File.Exists(cacheFile));
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(destination, "runtime")));
                await Assert.ThrowsAsync<IOException>(() => DataRelocation.CopyAsync(store, destination, token));
            }
            finally { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
        });
    }

    [Fact]
    public async Task ControllerReportsPersistedTransferHistoryForAllOrOneConnection()
    {
        var credential = SyntheticCredential.Create();
        await WithControllerAsync([credential], async (running, token) =>
        {
            var first = SyntheticCredential.Profile();
            var second = first with { Id = Guid.NewGuid(), Name = "Second", DriveLetter = first.DriveLetter == "Y" ? "Z" : "Y" };
            var history = new TransferHistoryStore(running.Root.Root);
            var now = DateTimeOffset.UtcNow;
            history.Record(first.Id, Guid.NewGuid(), new(1024, 1, 0, 0), now);
            history.Record(second.Id, Guid.NewGuid(), new(2048, 2, 1, 0), now);
            foreach (var profile in new[] { first, second })
                Assert.True((await running.SendAsync(new Request { Operation = "Save", Profile = profile, Credential = CredentialSubmission.ContainerSas(credential.Url) }, token)).Success);
            var all = await running.SendAsync(new Request { Operation = "Statistics", StatisticsDays = 30 }, token);
            Assert.True(all.Success);
            Assert.Equal(3072, all.Statistics!.Bytes);
            Assert.Equal(3, all.Statistics.CompletedTransfers);
            Assert.Equal(2, all.Statistics.Shares.Count);
            var selected = await running.SendAsync(new Request { Operation = "Statistics", ProfileId = second.Id, StatisticsDays = 7 }, token);
            Assert.True(selected.Success);
            Assert.Equal(2048, selected.Statistics!.Bytes);
            Assert.Equal(second.Id, Assert.Single(selected.Statistics.Shares).ProfileId);
            Assert.False((await running.SendAsync(new Request { Operation = "Statistics", ProfileId = Guid.NewGuid() }, token)).Success);
            var snapshot = Snapshot(running, await running.SendAsync(new Request { Operation = "Status" }, token));
            Assert.Equal(3072, snapshot.Transfers.Sum(summary => summary.Bytes));
            Assert.All(snapshot.Transfers, summary => Assert.Null(summary.BytesPerSecond));
            Assert.Equal(0, snapshot.EngineWorkerCount);
        });
    }

    [Fact]
    public async Task TransferHistoryIsProtectedRetainedAndAvailableThroughOfflineReports()
    {
        await WithControllerAsync([], async (running, token) =>
        {
            var profileId = Guid.NewGuid();
            var session = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var store = new TransferHistoryStore(running.Root.Root);
            store.Record(profileId, session, new(100, 2, 1, 10), now.AddDays(-10));
            store.Record(profileId, session, new(150, 3, 1, 5), now);
            var reopened = new TransferHistoryStore(running.Root.Root);
            Assert.Equal(150, reopened.Summary(profileId, false).Bytes);
            Assert.Null(reopened.Summary(profileId, false).BytesPerSecond);
            Assert.Equal(50, reopened.Report([profileId], 7, now).Bytes);
            Assert.Equal(150, reopened.Report([profileId], 30, now).Bytes);
            Assert.Equal(1, Assert.Single(reopened.Report([profileId], 7, now).Buckets).CompletedTransfers);
            reopened.Record(profileId, session, new(150, 3, 1, 0), now.AddSeconds(1));
            Assert.Equal(150, reopened.Summary(profileId, false).Bytes);
            var file = running.Root.PathFor("settings", "transfers-" + profileId.ToString("N") + ".bin");
            var protectedBytes = File.ReadAllBytes(file);
            Assert.False(Encoding.UTF8.GetString(protectedBytes).Contains("completedTransfers", StringComparison.Ordinal));
            File.WriteAllBytes(file, [1, 2, 3]);
            var corrupt = new TransferHistoryStore(running.Root.Root);
            Assert.True(corrupt.Summary(profileId, false).Unavailable);
            corrupt.Record(profileId, Guid.NewGuid(), new(999, 0, 0, 0), now.AddMinutes(1));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
            var report = await running.SendAsync(new Request { Operation = "Statistics" }, token);
            Assert.True(report.Success);
            Assert.Empty(Assert.IsType<TransferReport>(report.Statistics).Buckets);
            Assert.False((await running.SendAsync(new Request { Operation = "Statistics", StatisticsDays = 8 }, token)).Success);
        });
    }

    [Theory]
    [InlineData(AuthenticationKind.ContainerSas)]
    [InlineData(AuthenticationKind.AccountKey)]
    [InlineData(AuthenticationKind.MicrosoftEntra)]
    public async Task CloneCopiesSavedAccessToAnUnusedDriveWithoutMountingOrCopyingRecovery(AuthenticationKind kind)
    {
        var sas = kind == AuthenticationKind.MicrosoftEntra ? DelegationCredential() : SyntheticCredential.Create();
        var credential = kind switch
        {
            AuthenticationKind.AccountKey => CredentialSubmission.AccountKeyCredential(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64))),
            AuthenticationKind.MicrosoftEntra => CredentialSubmission.MicrosoftEntraSas(sas.Url),
            _ => CredentialSubmission.ContainerSas(sas.Url)
        };
        await WithControllerAsync([sas], async (running, token) =>
        {
            var source = Assert.Single(Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Save",
                Profile = SyntheticCredential.Profile() with
                {
                    Name = new string('N', 100), AuthenticationKind = kind, AutoMount = false, Prefix = "documents",
                    ReadOnly = kind != AuthenticationKind.AccountKey,
                    TenantId = kind == AuthenticationKind.MicrosoftEntra ? "11111111-1111-1111-1111-111111111111" : "",
                    SubscriptionId = kind == AuthenticationKind.MicrosoftEntra ? "22222222-2222-2222-2222-222222222222" : "",
                    ResourceGroupName = kind == AuthenticationKind.MicrosoftEntra ? "synthetic-rg" : ""
                },
                Credential = credential
            }, token)).Profiles);
            var store = running.Root.OpenStore();
            source = store.Save(source with { AutoMount = true }, null);
            store.WriteIntent(new MountIntent { ProfileId = source.Id, Identity = Validation.Identity(source), EngineVersion = EngineLocator.Version, Uncertain = true, WasWritable = !source.ReadOnly });
            var intent = store.ReadIntent(source.Id);
            var occupied = DriveInfo.GetDrives().Select(drive => drive.Name[..1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            occupied.Add(source.DriveLetter);

            var first = Snapshot(running, await running.SendAsync(new Request { Operation = "Clone", ProfileId = source.Id }, token));
            var copy = Assert.Single(first.Profiles, profile => profile.Id != source.Id);
            Assert.Equal(source, Assert.Single(first.Profiles, profile => profile.Id == source.Id));
            Assert.Equal(source with { Id = copy.Id, Name = copy.Name, DriveLetter = copy.DriveLetter, AutoMount = false, Revision = 1, CredentialRevision = 1 }, copy);
            Assert.EndsWith(" (copy)", copy.Name);
            Assert.InRange(copy.Name.Length, 1, 100);
            Assert.DoesNotContain(copy.DriveLetter, occupied);
            AssertUnmounted(first, copy);
            Assert.Equal(0, first.EngineWorkerCount);
            Assert.Null(store.ReadIntent(copy.Id));
            Assert.Equal(intent, store.ReadIntent(source.Id));
            Assert.False(Directory.Exists(running.Root.PathFor("cache", copy.Id.ToString("N"))));
            Assert.Equal(credential with { ExpectedRevision = 1 }, store.ReadCredential(copy.Id));

            var second = Snapshot(running, await running.SendAsync(new Request { Operation = "Clone", ProfileId = source.Id }, token));
            Assert.Equal(3, second.Profiles.Count);
            Assert.Equal(3, second.Profiles.Select(profile => profile.DriveLetter).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(3, second.Profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(second.Profiles.Where(profile => profile.Id != source.Id), profile => AssertUnmounted(second, profile));
            var serialized = JsonSerializer.Serialize(second, Wire.Json);
            Assert.DoesNotContain(sas.Url, serialized, StringComparison.Ordinal);
            if (credential.AccountKey is { } key) Assert.DoesNotContain(key, serialized, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CloneRejectsMissingProfilesAndExhaustedDriveLettersWithoutSaving()
    {
        var credential = SyntheticCredential.Create();
        await WithControllerAsync([credential], async (running, token) =>
        {
            AssertFailure(await running.SendAsync(new Request { Operation = "Clone", ProfileId = Guid.NewGuid() }, token), "not found");
            var store = running.Root.OpenStore();
            foreach (var code in Enumerable.Range('D', 23))
                store.Save(SyntheticCredential.Profile() with
                {
                    Id = Guid.NewGuid(), Name = "Reserved " + (char)code, DriveLetter = ((char)code).ToString(), AutoMount = false
                }, CredentialSubmission.ContainerSas(credential.Url));
            var before = Snapshot(running, await running.SendAsync(new Request { Operation = "Status" }, token));
            Assert.Equal(23, before.Profiles.Count);
            AssertFailure(await running.SendAsync(new Request { Operation = "Clone", ProfileId = before.Profiles[0].Id }, token), "No available drive letters");
            var after = Snapshot(running, await running.SendAsync(new Request { Operation = "Status" }, token));
            Assert.Equal(before.Profiles, after.Profiles);
            Assert.Equal(0, after.EngineWorkerCount);
            Assert.All(after.Profiles, profile => AssertUnmounted(after, profile));
            Assert.Empty(Directory.EnumerateDirectories(running.Root.PathFor("cache")));
        });
    }

    [Fact]
    public async Task CapabilityChecksUseRealEngineWithoutCreatingProfilesOrMounts()
    {
        await WithControllerAsync([], async (running, token) =>
        {
            var response = await running.SendAsync(new Request { Operation = "TestCapabilities" }, token);
            var snapshot = Snapshot(running, response);
            Assert.Empty(snapshot.Profiles);
            Assert.Empty(snapshot.Mounts);
            Assert.Equal(0, snapshot.EngineWorkerCount);
            Assert.True(snapshot.EngineAvailable);
            Assert.NotNull(response.Capabilities);
            foreach (var name in new[] { "Backend", "Engine", "Mount support", "Local storage" })
                Assert.True(Assert.Single(response.Capabilities, check => check.Name == name).Passed, name + " capability check failed.");
            Assert.Equal(!snapshot.Elevated, Assert.Single(response.Capabilities, check => check.Name == "Windows session").Passed);
            Assert.Equal(snapshot.WinFspInstalled, Assert.Single(response.Capabilities, check => check.Name == "WinFsp").Passed);
            Assert.Empty(Directory.EnumerateFiles(running.Root.Root, "capability-*.tmp"));
            var refreshed = Snapshot(running, await running.SendAsync(new Request { Operation = "Status" }, token));
            Assert.Equal(0, refreshed.EngineWorkerCount);
            Assert.Empty(refreshed.Mounts);
        });
    }

    [Fact]
    public async Task ActualControllerPersistsRenewsAndRemovesAccountKeyAndEntraCredentialsWithoutNetworkAccess()
    {
        var accountKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var replacementKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var delegation = DelegationCredential();
        var replacementDelegation = DelegationCredential();
        await WithControllerAsync([delegation, replacementDelegation], async (running, token) =>
        {
            var keyProfile = SyntheticCredential.Profile() with
            {
                AuthenticationKind = AuthenticationKind.AccountKey,
                AutoMount = false
            };
            var keySaved = Assert.Single(Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Save",
                Profile = keyProfile,
                Credential = CredentialSubmission.AccountKeyCredential(accountKey)
            }, token)).Profiles);
            Assert.Equal(AuthenticationKind.AccountKey, keySaved.AuthenticationKind);
            Assert.Equal(1, keySaved.CredentialRevision);
            Assert.Null(keySaved.ExpiresAt);

            var keyRenewed = Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Renew",
                ProfileId = keySaved.Id,
                Credential = CredentialSubmission.AccountKeyCredential(replacementKey, keySaved.CredentialRevision)
            }, token));
            var keyCurrent = Assert.Single(keyRenewed.Profiles);
            Assert.Equal(keySaved.Revision, keyCurrent.Revision);
            Assert.Equal(keySaved.CredentialRevision + 1, keyCurrent.CredentialRevision);
            var recoveredKey = running.Root.OpenStore().ReadCredential(keySaved.Id);
            Assert.True(string.Equals(replacementKey, recoveredKey.AccountKey, StringComparison.Ordinal), "The stored synthetic key differs; key text is not printed.");

            var entraProfile = SyntheticCredential.Profile() with
            {
                Id = Guid.NewGuid(),
                Name = "Synthetic Entra profile",
                DriveLetter = "Y",
                AuthenticationKind = AuthenticationKind.MicrosoftEntra,
                TenantId = "11111111-1111-1111-1111-111111111111",
                SubscriptionId = "22222222-2222-2222-2222-222222222222",
                ResourceGroupName = "synthetic-rg",
                AutoMount = false
            };
            var entraSaved = Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Save",
                Profile = entraProfile,
                Credential = CredentialSubmission.MicrosoftEntraSas(delegation.Url)
            }, token)).Profiles.Single(profile => profile.Id == entraProfile.Id);
            Assert.Equal(AuthenticationKind.MicrosoftEntra, entraSaved.AuthenticationKind);
            Assert.Equal(1, entraSaved.CredentialRevision);
            Assert.NotNull(entraSaved.ExpiresAt);

            var entraRenewed = Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Renew",
                ProfileId = entraSaved.Id,
                Credential = CredentialSubmission.MicrosoftEntraSas(replacementDelegation.Url, entraSaved.CredentialRevision)
            }, token)).Profiles.Single(profile => profile.Id == entraSaved.Id);
            Assert.Equal(entraSaved.Revision, entraRenewed.Revision);
            Assert.Equal(entraSaved.CredentialRevision + 1, entraRenewed.CredentialRevision);

            var removedKey = Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Remove", ProfileId = keySaved.Id
            }, token));
            Assert.DoesNotContain(removedKey.Profiles, profile => profile.Id == keySaved.Id);
            var removedAll = Snapshot(running, await running.SendAsync(new Request
            {
                Operation = "Remove", ProfileId = entraSaved.Id
            }, token));
            Assert.Empty(removedAll.Profiles);

            var keyBytes = Encoding.UTF8.GetBytes(replacementKey);
            foreach (var file in Directory.EnumerateFiles(running.Root.Root, "*", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(file, token); }
                catch (IOException) { continue; }
                Assert.True(bytes.AsSpan().IndexOf(keyBytes) < 0, "A controller file contains a synthetic account key in plaintext.");
            }
            var activeLock = running.Root.PathFor("runtime", "controller-" + AppPaths.SessionKey(running.Root.Root) + ".lock");
            running.Root.AssertNoPlaintext([delegation, replacementDelegation], activeLock);
        });
    }

    [Fact]
    public async Task ActualControllerSavesRejectsUnsafeChangesExportsAndRemovesAnUnmountedProfile()
    {
        var credential = SyntheticCredential.Create();
        var otherEndpoint = SyntheticCredential.Create(endpoint: "https://ctdotherlocal000.blob.core.windows.net");
        var otherContainer = SyntheticCredential.Create(container: "different-container");
        await WithControllerAsync([credential, otherEndpoint, otherContainer], async (running, token) =>
        {
            var info = Validation.ParseSas(credential.Url); // Parsing only; never Validate/Mount or an Azure request.
            var proposed = SyntheticCredential.Profile() with
            {
                Endpoint = info.Endpoint, Container = info.Container, ExpiresAt = info.ExpiresAt, AutoMount = false
            };
            var savedSnapshot = Snapshot(running, await running.SendAsync(new()
            {
                Operation = "Save",
                Profile = proposed,
                Credential = CredentialSubmission.ContainerSas(credential.Url)
            }, token));
            // Save returns the committed revision in Response.Snapshot, not a direct Profile property.
            var saved = Assert.Single(savedSnapshot.Profiles);
            Assert.Equal(proposed with { Revision = 1, CredentialRevision = 1, ExpiresAt = credential.ExpiresAt }, saved);
            AssertUnmounted(savedSnapshot, saved);
            var recordPath = running.Root.PathFor("profiles", saved.Id.ToString("N") + ".json");
            var committedBytes = await File.ReadAllBytesAsync(recordPath, token);

            var stale = await running.SendAsync(new()
            {
                Operation = "Save", Profile = proposed with { Name = "Stale edit" }
            }, token);
            AssertFailure(stale, "changed");
            AssertFailure(await running.SendAsync(new()
            {
                Operation = "Save", Profile = saved with { ReadOnly = false }
            }, token), "writable profile requires SAS");
            foreach (var replacement in new[] { otherEndpoint, otherContainer })
                AssertFailure(await running.SendAsync(new()
                {
                    Operation = "Renew",
                    ProfileId = saved.Id,
                    Credential = CredentialSubmission.ContainerSas(replacement.Url, saved.CredentialRevision)
                }, token), "target differ");

            var unchanged = Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, token));
            Assert.Equal(saved, Assert.Single(unchanged.Profiles));
            AssertUnmounted(unchanged, saved);
            Assert.Equal(committedBytes, await File.ReadAllBytesAsync(recordPath, token));
            Assert.False(File.Exists(recordPath + ".bak"));
            var secretUnchanged = running.Root.OpenStore().ReadSecret(saved.Id) == credential.Url;
            Assert.True(secretUnchanged, "Rejected requests must not replace the synthetic credential.");

            await AssertRedactedExportAsync(running, unchanged, saved, token);
            var removed = Snapshot(running, await running.SendAsync(new()
            {
                Operation = "Remove", ProfileId = saved.Id
            }, token));
            Assert.Empty(removed.Profiles);
            Assert.Empty(removed.Mounts);
            Assert.Empty(Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, token)).Profiles);
            Assert.Null(running.Root.OpenStore().Find(saved.Id));
            Assert.Throws<KeyNotFoundException>(() => running.Root.OpenStore().ReadSecret(saved.Id));
            // No mount intent or worker runtime/cache has ever been created for this fixture.
            Assert.Empty(Directory.EnumerateFileSystemEntries(running.Root.PathFor("intents")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(running.Root.PathFor("cache")));
        });
    }

    [Fact]
    public async Task ActualControllerSavesWritableProfileWhenSasAllowsMutations()
    {
        var credential = SyntheticCredential.Create(permissions: "racwdl");
        await WithControllerAsync([credential], async (running, token) =>
        {
            var info = Validation.ParseSas(credential.Url);
            var profile = SyntheticCredential.Profile() with
            {
                Endpoint = info.Endpoint,
                Container = info.Container,
                ExpiresAt = info.ExpiresAt,
                ReadOnly = false
            };
            var snapshot = Snapshot(running, await running.SendAsync(new()
            {
                Operation = "Save",
                Profile = profile,
                Credential = CredentialSubmission.ContainerSas(credential.Url)
            }, token));
            var saved = Assert.Single(snapshot.Profiles);
            Assert.False(saved.ReadOnly);
            AssertUnmounted(snapshot, saved);
        });
    }

    [Fact]
    public async Task InvalidRequestsAndMalformedFramesMustNotStopTheActualController()
    {
        await WithControllerAsync([], async (running, token) =>
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => running.Client.SendAsync(null!, token));
            foreach (var version in new[] { 0, Wire.Version + 1 })
                await Assert.ThrowsAsync<InvalidDataException>(() => running.Client.SendAsync(new()
                {
                    Operation = "Status", ProtocolVersion = version
                }, token)); // Client-side rejection, not a server failure response.

            foreach (var operation in new[] { "UnsupportedLocalIntegrationOperation", "", null })
                AssertFailure(await running.SendAsync(new() { Operation = operation! }, token), "Unsupported controller operation");
            AssertFailure(await running.SendAsync(new() { Operation = "Save" }, token), "Profile is required");
            AssertFailure(await running.SendAsync(new()
            {
                Operation = "Renew", ProfileId = Guid.NewGuid()
            }, token), "replacement credential is required");
            AssertFailure(await running.SendAsync(new()
            {
                Operation = "DiscoverContainers",
                StorageAccountName = "INVALID",
                Credential = CredentialSubmission.AccountKeyCredential(Convert.ToBase64String(new byte[64]))
            }, token), "lowercase Azure Storage account name");
            AssertFailure(await running.SendAsync(new()
            {
                Operation = "DiscoverContainers",
                StorageAccountName = "syntheticaccount",
                Credential = CredentialSubmission.AccountKeyCredential("invalid")
            }, token), "unsupported format");
            Assert.Empty(Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, token)).Profiles);

            // Send invalid JSON with a valid little-endian frame length to the actual server, not a mock.
            // Known source-level regression: PipeWire throws InvalidDataException (not IOException),
            // which Program's per-connection catch currently misses. Keep survival as a failing gate;
            // do not assert that crashing is correct or repair production code from this test.
            await using (var pipe = await ConnectOwnedPipeAsync(running, token))
            {
                byte[] malformed = [0, 0, 0, 0, (byte)'{', (byte)']'];
                BinaryPrimitives.WriteInt32LittleEndian(malformed, malformed.Length - sizeof(int));
                await pipe.WriteAsync(malformed, token);
                await pipe.FlushAsync(token);
                Response? rejection = null;
                try { rejection = await PipeWire.ReadAsync<Response>(pipe, token); }
                catch (EndOfStreamException) { } // Closing only this bad connection is also a valid rejection.
                catch (IOException exception) when ((exception.HResult & 0xffff) is 109 or 232 or 233) { }
                if (rejection is not null) AssertFailure(rejection);
            }

            Assert.False(running.Process.HasExited, "A malformed frame must not terminate the owned controller.");
            // No retry/restart here: a broken pipe, early process exit or timeout is a real regression.
            var after = Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, token));
            Assert.Empty(after.Profiles);
            Assert.Empty(after.Mounts);
        });
    }

    [Fact]
    public async Task ConcurrentConnectionsReturnIndependentResponsesAndOnlyOneRevisionWins()
    {
        var credential = SyntheticCredential.Create();
        await WithControllerAsync([credential], async (running, token) =>
        {
            var statuses = await SendConcurrentlyAsync(running,
                new() { Operation = "Status" }, new() { Operation = "Status" }, token);
            foreach (var response in statuses)
            {
                var snapshot = Snapshot(running, response);
                Assert.Empty(snapshot.Profiles);
                Assert.Empty(snapshot.Mounts);
            }

            var first = Assert.Single(Snapshot(running, await running.SendAsync(new()
            {
                Operation = "Save",
                Profile = SyntheticCredential.Profile() with { AutoMount = false },
                Credential = CredentialSubmission.ContainerSas(credential.Url)
            }, token)).Profiles);
            var edits = new[] { first with { Name = "Concurrent edit A" }, first with { Name = "Concurrent edit B" } };
            var responses = await SendConcurrentlyAsync(running,
                new() { Operation = "Save", Profile = edits[0] }, new() { Operation = "Save", Profile = edits[1] }, token);
            var winner = Assert.Single(responses, response => response.Success);
            AssertFailure(Assert.Single(responses, response => !response.Success), "changed");
            var winnerIndex = Array.FindIndex(responses, response => response.Success);
            var committed = Assert.Single(Snapshot(running, winner).Profiles);
            Assert.Equal(edits[winnerIndex] with { Revision = first.Revision + 1 }, committed);
            var current = Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, token));
            Assert.Equal(committed, Assert.Single(current.Profiles));
            AssertUnmounted(current, committed);
            // Leave this clean, non-automount profile saved: Shutdown must also allow unmounted profiles.
        });
    }

    private static async Task<Response[]> SendConcurrentlyAsync(RunningController running,
        Request first, Request second, CancellationToken token)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = SendAsync(first);
        var secondTask = SendAsync(second);
        start.SetResult();
        return await Task.WhenAll(firstTask, secondTask);

        async Task<Response> SendAsync(Request request)
        {
            // Separate clients/connections, released together, with no test-side serialization or replay.
            var client = new ControllerClient(running.Root.Root);
            await start.Task.WaitAsync(token);
            return await running.SendAsync(request, token, client);
        }
    }

    private static SyntheticCredential DelegationCredential()
    {
        var signature = "SYNTHETIC-NOT-A-REAL-DELEGATION-" + Guid.NewGuid().ToString("N");
        var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expires = DateTimeOffset.UtcNow.AddDays(1);
        var url = SyntheticCredential.Endpoint + "/" + SyntheticCredential.Container +
            "?sv=2025-01-05&sr=c&sp=rl&spr=https" +
            "&st=" + Uri.EscapeDataString(starts.ToString("O")) +
            "&se=" + Uri.EscapeDataString(expires.ToString("O")) +
            "&sig=" + signature +
            "&skoid=11111111-1111-1111-1111-111111111111" +
            "&sktid=22222222-2222-2222-2222-222222222222" +
            "&skt=" + Uri.EscapeDataString(starts.ToString("O")) +
            "&ske=" + Uri.EscapeDataString(expires.ToString("O")) +
            "&sks=b&skv=2025-01-05";
        return new SyntheticCredential(url, signature, expires);
    }

    private static async Task AssertRedactedExportAsync(RunningController running, AppSnapshot snapshot,
        Profile profile, CancellationToken token)
    {
        var before = DateTimeOffset.UtcNow;
        var response = await running.SendAsync(new() { Operation = "Export" }, token);
        var after = DateTimeOffset.UtcNow;
        Assert.True(response.Success);
        Assert.Null(response.Snapshot);
        Assert.NotNull(response.ExportPath);
        Assert.True(Path.IsPathFullyQualified(response.ExportPath));
        var path = running.Root.PathFor("diagnostics", Path.GetFileName(response.ExportPath));
        AssertPath(path, response.ExportPath);
        var bytes = await File.ReadAllBytesAsync(path, token);
        running.AssertNoPlaintext(bytes);
        using var document = JsonDocument.Parse(bytes);
        var report = document.RootElement;
        AssertFields(report, "schemaVersion", "generatedAt", "version", "rcloneVersion", "winFspInstalled", "engineAvailable", "elevated", "mounts");
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.InRange(report.GetProperty("generatedAt").GetDateTimeOffset(), before, after);
        Assert.Equal(snapshot.Version, report.GetProperty("version").GetString());
        Assert.Equal(EngineLocator.Version, report.GetProperty("rcloneVersion").GetString());
        Assert.Equal(snapshot.WinFspInstalled, report.GetProperty("winFspInstalled").GetBoolean());
        Assert.Equal(snapshot.EngineAvailable, report.GetProperty("engineAvailable").GetBoolean());
        Assert.Equal(snapshot.Elevated, report.GetProperty("elevated").GetBoolean());
        var mount = Assert.Single(report.GetProperty("mounts").EnumerateArray());
        AssertFields(mount, "index", "phase", "uploads", "observedAt", "queued", "uploading", "cacheBytes", "recoveryRequired");
        Assert.Equal(0, mount.GetProperty("index").GetInt32());
        Assert.Equal(nameof(MountPhase.Unmounted), mount.GetProperty("phase").GetString());
        Assert.Equal(nameof(UploadState.Unknown), mount.GetProperty("uploads").GetString());
        Assert.False(mount.GetProperty("recoveryRequired").GetBoolean());
        foreach (var name in new[] { "observedAt", "queued", "uploading", "cacheBytes" })
            Assert.Equal(JsonValueKind.Null, mount.GetProperty(name).ValueKind);

        // Exact field allowlists above exclude SAS/ciphertext, profiles, source/queue/file names and paths.
        // Also reject their values, including a JSON-escaped local path, without dumping export contents.
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var value in new[] { profile.Name, profile.Endpoint, profile.Container, profile.Prefix,
            profile.Id.ToString("D"), profile.Id.ToString("N"), profile.RemoteName, profile.RemotePath, running.Root.Root })
            Assert.False(text.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                text.Contains(JsonSerializer.Serialize(value, Wire.Json), StringComparison.OrdinalIgnoreCase),
                "The diagnostic export contains fixture identity or source details.");
    }

    private static AppSnapshot Snapshot(RunningController running, Response response)
    {
        Assert.True(response.Success, "The controller request did not succeed.");
        Assert.Null(response.ExportPath);
        var snapshot = Assert.IsType<AppSnapshot>(response.Snapshot);
        AssertPath(running.Root.Root, snapshot.DataRoot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Version));
        Assert.Equal(AppPaths.IsElevated, snapshot.Elevated); // Elevated control is supported; mounts are not attempted.
        Assert.Equal(AppPaths.WinFspInstalled, snapshot.WinFspInstalled); // Either installed or absent is valid.
        Assert.True(snapshot.WritableEnabled);
        return snapshot;
    }

    private static void AssertUnmounted(AppSnapshot snapshot, Profile profile)
    {
        var mount = Assert.Single(snapshot.Mounts, status => status.ProfileId == profile.Id);
        Assert.Equal(profile.Id, mount.ProfileId);
        Assert.Equal(MountPhase.Unmounted, mount.Phase);
        Assert.False(mount.RecoveryRequired);
        Assert.Empty(mount.Queue);
    }

    private static void AssertFailure(Response response, string? message = null)
    {
        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
        Assert.Null(response.Snapshot);
        Assert.Null(response.ExportPath);
        if (message is not null) Assert.Contains(message, response.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFields(JsonElement value, params string[] fields) =>
        Assert.Equal(fields.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static async Task WithControllerAsync(SyntheticCredential[] credentials,
        Func<RunningController, CancellationToken, Task> assertions)
    {
        var root = LocalTestRoot.Create();
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Root));
        Assert.NotEqual(AppPaths.PipeName(root.Root), AppPaths.PipeName(root.PathFor("distinct-root")));
        var output = Path.GetRelativePath(root.Workspace, AppContext.BaseDirectory).Split(Path.DirectorySeparatorChar);
        var configuration = output.Contains("release", StringComparer.OrdinalIgnoreCase) ? "release" :
            output.Contains("debug", StringComparer.OrdinalIgnoreCase) ? "debug" :
            throw new InvalidOperationException("Run the integration assembly from debug or release build artifacts.");
        var executable = Path.Combine(root.Workspace, "artifacts", "bin", "ContainerToDrive.Controller", configuration, "ContainerToDrive.Controller.exe");
        Assert.True(File.Exists(executable), "Build the matching controller executable before running these tests; no fallback or build is performed here.");
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(root.Root); // Explicit fresh GUID root overrides any ambient data-root setting.
        using var process = new Process { StartInfo = start };
        var running = new RunningController(root, process, new ControllerClient(root.Root), credentials);
        Task<string> stdout = Task.FromResult("");
        Task<string> stderr = Task.FromResult("");
        var started = false;
        var startedAfter = DateTime.UtcNow;
        try
        {
            started = process.Start(); // Direct apphost launch, never EnsureStartedAsync or a shell/elevation workaround.
            Assert.True(started);
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            Assert.False(process.SafeHandle.IsInvalid); // Retain this exact process handle across shutdown/PID reuse.
            Assert.False(process.HasExited);
            Assert.True(process.StartTime.ToUniversalTime() >= startedAfter);
            using var current = Process.GetCurrentProcess();
            Assert.Equal(current.SessionId, process.SessionId);

            var initial = await WaitForReadyAsync(running);
            process.Refresh();
            AssertPath(executable, process.MainModule?.FileName);
            Assert.Empty(initial.Profiles);
            Assert.Empty(initial.Mounts);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await AssertWireStatusAndOwnedPidAsync(running, initial, deadline.Token);
            await assertions(running, deadline.Token);

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stopped = await running.SendAsync(new() { Operation = "Shutdown" }, shutdown.Token);
            Assert.True(stopped.Success, "Shutdown must allow empty or clean, unmounted fixtures.");
            Assert.Null(stopped.Snapshot);
            Assert.Null(stopped.ExportPath);
            await process.WaitForExitAsync(shutdown.Token);
            Assert.True(process.HasExited);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (started)
            {
                await CleanupOwnedControllerAsync(running);
                Assert.True(process.HasExited, "The exact test controller must not be left running.");
                running.AssertNoPlaintext(Encoding.UTF8.GetBytes(await stdout.WaitAsync(TimeSpan.FromSeconds(5))));
                running.AssertNoPlaintext(Encoding.UTF8.GetBytes(await stderr.WaitAsync(TimeSpan.FromSeconds(5))));
                var singleton = root.PathFor("runtime", "controller-" + AppPaths.SessionKey(root.Root) + ".lock");
                if (File.Exists(singleton))
                {
                    using var released = new FileStream(singleton, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.Equal(0L, released.Length);
                }
            }
            root.AssertNoPlaintext(credentials); // Includes backups and the now-readable singleton lock; retain all roots.
        }
    }

    private static async Task<AppSnapshot> WaitForReadyAsync(RunningController running)
    {
        // Cold startup verifies/hashes the large pinned engine archive and executable before listening.
        // Allow the full 15 seconds; do not abandon an accepted first Status with a tiny per-attempt timer.
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            ready.Token.ThrowIfCancellationRequested();
            Assert.False(running.Process.HasExited, "The owned controller exited before accepting Status.");
            try { return Snapshot(running, await running.SendAsync(new() { Operation = "Status" }, ready.Token)); }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                // FileNotFoundException is an IOException. InvalidDataException, authentication,
                // assertion and cancellation failures must propagate immediately.
                await Task.Delay(100, ready.Token);
            }
        }
    }

    private static async Task AssertWireStatusAndOwnedPidAsync(RunningController running, AppSnapshot expected, CancellationToken token)
    {
        await using var pipe = await ConnectOwnedPipeAsync(running, token);
        var singleton = running.Root.PathFor("runtime", "controller-" + AppPaths.SessionKey(running.Root.Root) + ".lock");
        Assert.True(File.Exists(singleton), "The controller must hold its singleton in the isolated root.");
        var busy = Assert.Throws<IOException>(() =>
        {
            using var lease = new FileStream(singleton, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Assert.Equal(32, busy.HResult & 0xffff); // ERROR_SHARING_VIOLATION, not a missing/unreadable lock.
        await PipeWire.WriteAsync(pipe, new Request { Operation = "Status" }, token);
        var response = await PipeWire.ReadAsync<JsonElement>(pipe, token);
        Assert.True(response.GetProperty("success").GetBoolean());
        var snapshot = response.GetProperty("snapshot");
        // Inspect the actual wire fields too: typed defaults alone cannot prove that a field was sent.
        Assert.Equal(expected.Version, snapshot.GetProperty("version").GetString());
        AssertPath(running.Root.Root, snapshot.GetProperty("dataRoot").GetString());
        Assert.Equal(expected.WinFspInstalled, snapshot.GetProperty("winFspInstalled").GetBoolean());
        Assert.Equal(expected.EngineAvailable, snapshot.GetProperty("engineAvailable").GetBoolean());
        Assert.Equal(expected.Elevated, snapshot.GetProperty("elevated").GetBoolean());
        Assert.True(snapshot.GetProperty("writableEnabled").GetBoolean());
        Assert.Empty(snapshot.GetProperty("profiles").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("mounts").EnumerateArray());
    }

    private static async Task<NamedPipeClientStream> ConnectOwnedPipeAsync(RunningController running, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", AppPaths.PipeName(running.Root.Root), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(5000, token);
            // Not merely a same-name/same-user controller. Check before sending even a nonsecret command.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner) ||
                owner != (uint)running.Process.Id || running.Process.HasExited)
                throw new UnauthorizedAccessException("The pipe does not belong to the exact test controller.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task CleanupOwnedControllerAsync(RunningController running)
    {
        if (running.Process.HasExited) return;
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Cleanup may follow a failed readiness/identity assertion. Verify the exact PID on THIS
            // connection, then use that same pipe for Shutdown; never stop an unexpected existing peer.
            await using (var pipe = await ConnectOwnedPipeAsync(running, shutdown.Token))
            {
                await PipeWire.WriteAsync(pipe, new Request { Operation = "Shutdown" }, shutdown.Token);
                var response = await PipeWire.ReadAsync<Response>(pipe, shutdown.Token);
                running.AssertNoPlaintext(JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json));
            }
            await running.Process.WaitForExitAsync(shutdown.Token);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or InvalidDataException) { }
        finally
        {
            // Fresh empty root + no Mount/Validate/AutoMount requests means only nonmounted fixtures
            // can be owned here. Never enumerate by name, reopen a PID, kill a tree, or touch another root.
            if (!running.Process.HasExited)
            {
                try { running.Process.Kill(entireProcessTree: false); }
                catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception) &&
                    running.Process.HasExited) { }
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await running.Process.WaitForExitAsync(exit.Token);
            }
        }
    }

    private static void AssertPath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)), ignoreCase: true);
    }

    private sealed record RunningController(LocalTestRoot Root, Process Process, ControllerClient Client,
        SyntheticCredential[] Credentials)
    {
        internal async Task<Response> SendAsync(Request request, CancellationToken token, ControllerClient? client = null)
        {
            // Guard the fixture's no-network/no-driver contract even as more cases are added later.
            Assert.False(request.Operation is "Validate" or "Mount" or "Refresh" or "Disconnect");
            Assert.False(request.Profile?.AutoMount == true);
            var response = await (client ?? Client).SendAsync(request, token);
            AssertNoPlaintext(JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json));
            return response;
        }

        internal void AssertNoPlaintext(byte[] bytes)
        {
            foreach (var credential in Credentials) LocalTestRoot.AssertNoPlaintext(bytes, credential);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}