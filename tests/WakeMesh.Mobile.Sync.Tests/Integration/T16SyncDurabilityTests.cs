using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Sync.Tests.Fakes;
using WakeMesh.Mobile.Sync.Tests.TestDoubles;
using Xunit.Abstractions;

namespace WakeMesh.Mobile.Sync.Tests.Integration;

/// <summary>
/// Permanent regressions from independent behavioral probes (real in-process API + SQLite,
/// real JSON file stores, fake OS scheduler — NOT native Android AlarmManager).
/// Covers: strict cancel-then-delete on tombstone, no success ACK/commit while an OS alarm survives,
/// deterministic binding-prefixed local ids, prefix-based cleanup across process recreation,
/// pre-validation of desired items before any side effect, and binding-scope cleanup API.
/// </summary>
public sealed class T16SyncDurabilityTests : IClassFixture<SyncTestApiFactory>, IDisposable
{
    private readonly SyncTestApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t16sync-" + Guid.NewGuid().ToString("N"));

    public T16SyncDurabilityTests(SyncTestApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort test cleanup */ }
    }

    private sealed class FailOnceManifestStore : ICloudSyncManifestStore
    {
        private readonly ICloudSyncManifestStore _inner;
        private int _remainingFailures;

        public FailOnceManifestStore(ICloudSyncManifestStore inner, int failures)
        {
            _inner = inner;
            _remainingFailures = failures;
        }

        public Task<CloudSyncManifest> LoadAsync(CancellationToken cancellationToken = default)
            => _inner.LoadAsync(cancellationToken);

        public Task SaveAsync(CloudSyncManifest manifest, CancellationToken cancellationToken = default)
        {
            if (_remainingFailures-- > 0)
            {
                throw new IOException("simulated manifest commit failure");
            }

            return _inner.SaveAsync(manifest, cancellationToken);
        }
    }

    private sealed class CancelingScheduler : IAlarmScheduler
    {
        private readonly IAlarmScheduler _inner;
        public CancellationTokenSource? Cts;

        public CancelingScheduler(IAlarmScheduler inner) => _inner = inner;

        public async Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
        {
            await _inner.ScheduleAlarmAsync(alarm, fireTime);
            Cts?.Cancel();
        }

        public Task CancelAlarmAsync(string alarmId) => _inner.CancelAlarmAsync(alarmId);
    }

    private sealed class Process
    {
        public required JsonFileAlarmStore Store;
        public required AlarmCoordinator Coordinator;
        public required ICloudSyncManifestStore Manifest;
        public required DurableAppliedRevisionStore Revisions;
        public required MobileDesiredApplier Applier;
        public required DesiredSyncOrchestrator Orchestrator;
        public required CloudSyncBinding Binding;
    }

    // "Process" = fresh instances over the same files; the OS (fake scheduler) persists across processes.
    private Process NewProcess(
        WakeMeshApiClient client,
        Guid serverDeviceId,
        IAlarmScheduler os,
        Func<ICloudSyncManifestStore, ICloudSyncManifestStore>? wrapManifest = null,
        string url = "https://api.wakemesh.local")
    {
        var binding = new CloudSyncBinding(url, "owner-user", serverDeviceId, 1);
        var store = new JsonFileAlarmStore(Path.Combine(_dir, "alarms.json"));
        var coordinator = new AlarmCoordinator(store, new AlarmScheduleCalculator(new FakeHolidayService()), os);
        ICloudSyncManifestStore manifest = new FileCloudSyncManifestStore(binding, Path.Combine(_dir, $"manifest_{binding.ToStorageKey()}.json"));
        if (wrapManifest is not null)
        {
            manifest = wrapManifest(manifest);
        }

        var revisions = new DurableAppliedRevisionStore(binding, Path.Combine(_dir, $"rev_{binding.ToStorageKey()}.json"));
        var applier = new MobileDesiredApplier(binding, coordinator, manifest);

        return new Process
        {
            Store = store,
            Coordinator = coordinator,
            Manifest = manifest,
            Revisions = revisions,
            Applier = applier,
            Orchestrator = new DesiredSyncOrchestrator(client, applier, revisions),
            Binding = binding,
        };
    }

    private async Task<(WakeMeshApiClient Client, Guid DeviceId)> RegisterAsync()
    {
        var client = TestClientHelper.CreateApiClient(
            _factory.CreateClient(),
            new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()),
            new MemoryDeviceCredentialStore());
        var registration = await client.RegisterDeviceAsync("t16-probe");
        Assert.True(registration.IsSuccess, registration.ErrorCode);
        return (client, registration.Value!.Id);
    }

    private static async Task<long> CreateGroupAsync(WakeMeshApiClient client, Guid deviceId, string groupId)
    {
        var result = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            groupId,
            "G " + groupId,
            true,
            "weekly",
            // API/Client/Mobile wire: 0=Sun..6=Sat. Mon–Fri = 1..5.
            new[] { 1, 2, 3, 4, 5 },
            null,
            "Asia/Tokyo",
            false,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:45", "s", 80, true, true) }));
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Group.Version;
    }

    private static async Task SeedManualAsync(AlarmCoordinator coordinator)
    {
        await coordinator.SaveAndScheduleAsync(new LocalAlarmModel
        {
            Id = "manual-1",
            Name = "manual",
            IsEnabled = true,
            Time = new TimeOnly(5, 0),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday,
            TimeZoneId = "Asia/Tokyo",
        });
    }

    private static async Task<long?> ServerAppliedAsync(WakeMeshApiClient client, Guid deviceId)
        => (await client.GetDeviceAsync(deviceId)).Value!.AppliedRevision;

    private static string SyncLocalId(CloudSyncBinding binding, Guid scheduleId)
        => SyncLocalAlarmIdentity.GetLocalAlarmId(binding, scheduleId);

    [Fact]
    public async Task P1_CancelFailure_OnTombstone_NoSuccessAck_NoCommit_AndRetryCleansUp()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var first = NewProcess(client, deviceId, os);
        await SeedManualAsync(first.Coordinator);

        var version = await CreateGroupAsync(client, deviceId, "g1");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await first.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        var syncId = (await first.Coordinator.GetAllAsync()).Single(a => a.Id != "manual-1").Id;
        Assert.Equal(SyncLocalId(first.Binding, (await client.GetDesiredAsync(deviceId)).Value!.Items.Single().ScheduleId), syncId);

        var appliedBefore = await ServerAppliedAsync(client, deviceId);
        var durableBefore = await first.Revisions.GetAppliedRevisionAsync(deviceId);

        // OS cancellation fails exactly when the server retires the schedule.
        os.FailOnCancelIds.Add(syncId);
        Assert.True((await client.DeleteAlarmGroupAsync("g1", ifMatchVersion: version)).IsSuccess);

        var outcome = await first.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"P1 cancel-failed status={outcome.Status} osStillHas={os.ScheduledAlarms.ContainsKey(syncId)} " +
                          $"storeHas={(await first.Coordinator.GetByIdAsync(syncId)) is not null} " +
                          $"manifest={((await first.Manifest.LoadAsync()).Schedules.Count)} durable={await first.Revisions.GetAppliedRevisionAsync(deviceId)}");

        Assert.NotEqual(DesiredSyncStatus.AppliedAcknowledged, outcome.Status);
        Assert.Equal(durableBefore, await first.Revisions.GetAppliedRevisionAsync(deviceId));
        Assert.Equal(appliedBefore, await ServerAppliedAsync(client, deviceId));
        Assert.True(os.ScheduledAlarms.ContainsKey(syncId));
        Assert.NotNull(await first.Coordinator.GetByIdAsync(syncId));

        // Retry from a fresh process with a working OS cancel: the ghost is collected.
        os.FailOnCancelIds.Clear();
        var retry = NewProcess(client, deviceId, os);
        var retryOutcome = await retry.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"P1 retry status={retryOutcome.Status} ghostInOs={os.ScheduledAlarms.ContainsKey(syncId)} manualOk={os.ScheduledAlarms.ContainsKey("manual-1")}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, retryOutcome.Status);
        Assert.False(os.ScheduledAlarms.ContainsKey(syncId));
        Assert.Null(await retry.Coordinator.GetByIdAsync(syncId));
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));
    }

    [Fact]
    public async Task P2_OsScheduleFailure_ThenTombstone_LeavesNoUntrackedAlarm_AndNeverAcksSuccess()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var first = NewProcess(client, deviceId, os);
        await SeedManualAsync(first.Coordinator);

        var version = await CreateGroupAsync(client, deviceId, "g2");
        var scheduleId = (await client.GetDesiredAsync(deviceId)).Value!.Items.Single().ScheduleId;
        var expectedLocalId = SyncLocalId(first.Binding, scheduleId);

        os.FailOnScheduleIds.Add(expectedLocalId);
        var outcome = await first.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"P2 first status={outcome.Status} storeHas={(await first.Coordinator.GetByIdAsync(expectedLocalId))?.OsRegistrationStatus} " +
                          $"manifestHas={(await first.Manifest.LoadAsync()).Schedules.ContainsKey(scheduleId)} durable={await first.Revisions.GetAppliedRevisionAsync(deviceId)}");

        Assert.Equal(DesiredSyncStatus.ApplyFailedAcknowledged, outcome.Status);
        Assert.Null(await first.Revisions.GetAppliedRevisionAsync(deviceId));
        Assert.False((await first.Manifest.LoadAsync()).Schedules.ContainsKey(scheduleId));

        // Server retires the group; a fresh process must still collect the store-only orphan.
        os.FailOnScheduleIds.Clear();
        Assert.True((await client.DeleteAlarmGroupAsync("g2", ifMatchVersion: version)).IsSuccess);

        var retry = NewProcess(client, deviceId, os);
        var retryOutcome = await retry.Orchestrator.SyncDeviceAsync(deviceId);
        var orphan = await retry.Coordinator.GetByIdAsync(expectedLocalId);
        await retry.Coordinator.RescheduleAllActiveAsync(); // boot/reschedule path must not re-arm it

        _output.WriteLine($"P2 after tombstone status={retryOutcome.Status} orphanInStore={orphan is not null} " +
                          $"orphanInOsAfterBoot={os.ScheduledAlarms.ContainsKey(expectedLocalId)} manualOk={os.ScheduledAlarms.ContainsKey("manual-1")}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, retryOutcome.Status);
        Assert.Null(orphan);
        Assert.False(os.ScheduledAlarms.ContainsKey(expectedLocalId));
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));
    }

    [Fact]
    public async Task P3_ManifestCommitFailure_NeverAcksSuccess_AndRestartCollectsPrefixedOrphans()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var first = NewProcess(client, deviceId, os, manifest => new FailOnceManifestStore(manifest, 1));
        await SeedManualAsync(first.Coordinator);

        var version = await CreateGroupAsync(client, deviceId, "g3");
        var outcome = await first.Orchestrator.SyncDeviceAsync(deviceId);
        var syncIds = (await first.Coordinator.GetAllAsync()).Where(a => a.Id != "manual-1").Select(a => a.Id).ToList();
        _output.WriteLine($"P3 first status={outcome.Status} code={outcome.ErrorCode} osSync={syncIds.Count(os.ScheduledAlarms.ContainsKey)} " +
                          $"durable={await first.Revisions.GetAppliedRevisionAsync(deviceId)} serverApplied={await ServerAppliedAsync(client, deviceId)}");

        Assert.NotEqual(DesiredSyncStatus.AppliedAcknowledged, outcome.Status);
        Assert.Null(await first.Revisions.GetAppliedRevisionAsync(deviceId));
        Assert.Single(syncIds);
        Assert.True(os.ScheduledAlarms.ContainsKey(syncIds[0]));

        Assert.True((await client.DeleteAlarmGroupAsync("g3", ifMatchVersion: version)).IsSuccess);

        var retry = NewProcess(client, deviceId, os);
        var retryOutcome = await retry.Orchestrator.SyncDeviceAsync(deviceId);
        var leftover = syncIds.Where(os.ScheduledAlarms.ContainsKey).ToList();
        _output.WriteLine($"P3 restart status={retryOutcome.Status} untrackedInOs={leftover.Count} manualOk={os.ScheduledAlarms.ContainsKey("manual-1")}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, retryOutcome.Status);
        Assert.Empty(leftover);
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));
        Assert.DoesNotContain(await retry.Coordinator.GetAllAsync(), a => a.Id != "manual-1");
    }

    [Fact]
    public async Task P4_CancelMidApply_NoAck_ThenRetryApplies_AndReAckMatchesDurable()
    {
        var (client, deviceId) = await RegisterAsync();
        var fake = new FakeAlarmScheduler();
        var os = new CancelingScheduler(fake);
        var first = NewProcess(client, deviceId, os);
        await SeedManualAsync(first.Coordinator);
        await CreateGroupAsync(client, deviceId, "g4a");
        await CreateGroupAsync(client, deviceId, "g4b");

        using var cts = new CancellationTokenSource();
        os.Cts = cts;
        var canceled = await first.Orchestrator.SyncDeviceAsync(deviceId, cts.Token);
        _output.WriteLine($"P4 cancel status={canceled.Status} durable={await first.Revisions.GetAppliedRevisionAsync(deviceId)} " +
                          $"serverApplied={await ServerAppliedAsync(client, deviceId)} osCount={fake.ScheduledAlarms.Count}");

        Assert.NotEqual(DesiredSyncStatus.AppliedAcknowledged, canceled.Status);
        Assert.Null(await first.Revisions.GetAppliedRevisionAsync(deviceId));

        os.Cts = null;
        var retry = NewProcess(client, deviceId, os);
        var applied = await retry.Orchestrator.SyncDeviceAsync(deviceId);
        var syncAlarms = (await retry.Coordinator.GetAllAsync()).Where(a => a.Id != "manual-1").ToList();
        _output.WriteLine($"P4 retry status={applied.Status} syncAlarms={syncAlarms.Count} " +
                          $"manifest={(await retry.Manifest.LoadAsync()).Schedules.Count}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, applied.Status);
        Assert.Equal(2, syncAlarms.Count);
        Assert.All(syncAlarms, a => Assert.StartsWith(SyncLocalAlarmIdentity.GetBindingPrefix(retry.Binding), a.Id, StringComparison.Ordinal));

        // Crash after commit / before ACK: equal revision re-ACKs with the fresh receipt, no re-apply.
        var osCountBefore = fake.ScheduledAlarms.Count;
        var reAck = NewProcess(client, deviceId, os);
        var reAckOutcome = await reAck.Orchestrator.SyncDeviceAsync(deviceId);
        var durable = await reAck.Revisions.GetAppliedRevisionAsync(deviceId);
        _output.WriteLine($"P4 reAck status={reAckOutcome.Status} ackRev={reAckOutcome.Ack?.AppliedRevision} durable={durable} " +
                          $"server={await ServerAppliedAsync(client, deviceId)}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, reAckOutcome.Status);
        Assert.Equal(durable, reAckOutcome.Ack!.AppliedRevision);
        Assert.Equal(osCountBefore, fake.ScheduledAlarms.Count);
    }

    [Fact]
    public async Task P5_LegacyBindingCloudAlarms_AreRemovedByCleanupBindingAsync_AndManualAlarmSurvives()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var oldBindingProcess = NewProcess(client, deviceId, os);
        await SeedManualAsync(oldBindingProcess.Coordinator);

        await CreateGroupAsync(client, deviceId, "g5");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await oldBindingProcess.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        // Settings URL changed: the same server device now syncs under a different binding scope.
        var newBindingProcess = NewProcess(client, deviceId, os, url: "https://api2.wakemesh.local");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await newBindingProcess.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        var allAfterUrlChange = await oldBindingProcess.Coordinator.GetAllAsync();
        var oldScopeAlarms = allAfterUrlChange.Where(a => a.Id.StartsWith(SyncLocalAlarmIdentity.GetBindingPrefix(oldBindingProcess.Binding), StringComparison.Ordinal)).ToList();
        var newScopeAlarms = allAfterUrlChange.Where(a => a.Id.StartsWith(SyncLocalAlarmIdentity.GetBindingPrefix(newBindingProcess.Binding), StringComparison.Ordinal)).ToList();
        _output.WriteLine($"P5 after url change: oldScope={oldScopeAlarms.Count} newScope={newScopeAlarms.Count} osSync={os.ScheduledAlarms.Keys.Count(k => k.StartsWith("sync-", StringComparison.Ordinal))}");

        Assert.Single(oldScopeAlarms);
        Assert.Single(newScopeAlarms);

        // Scope cleanup API (UI wiring is a follow-up outside this task): strict cancel + manifest/revision clear.
        var cleanup = new CloudSyncBindingCleanup(
            oldBindingProcess.Binding,
            oldBindingProcess.Coordinator,
            oldBindingProcess.Manifest,
            oldBindingProcess.Revisions);

        var result = await cleanup.CleanupBindingAsync();
        _output.WriteLine($"P5 cleanup success={result.Succeeded} failures={result.Failures.Count}");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Null(await oldBindingProcess.Revisions.GetAppliedRevisionAsync(deviceId));
        Assert.Empty((await oldBindingProcess.Manifest.LoadAsync()).Schedules);

        var remaining = await oldBindingProcess.Coordinator.GetAllAsync();
        Assert.DoesNotContain(remaining, a => a.Id.StartsWith(SyncLocalAlarmIdentity.GetBindingPrefix(oldBindingProcess.Binding), StringComparison.Ordinal));
        Assert.Contains(remaining, a => a.Id == "manual-1");
        Assert.Contains(remaining, a => a.Id.StartsWith(SyncLocalAlarmIdentity.GetBindingPrefix(newBindingProcess.Binding), StringComparison.Ordinal));
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));
        Assert.DoesNotContain(os.ScheduledAlarms.Keys, k => oldScopeAlarms.Any(a => a.Id == k));
        Assert.Contains(newScopeAlarms[0].Id, os.ScheduledAlarms.Keys);
    }

    [Fact]
    public async Task CleanupBindingAsync_StrictCancelFailure_ReportsHonestFailure_AndKeepsManifestAndRevision()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var process = NewProcess(client, deviceId, os);

        await CreateGroupAsync(client, deviceId, "g6");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await process.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        var alarm = (await process.Coordinator.GetAllAsync()).Single();
        var durableBefore = await process.Revisions.GetAppliedRevisionAsync(deviceId);

        os.FailOnCancelIds.Add(alarm.Id);

        var cleanup = new CloudSyncBindingCleanup(process.Binding, process.Coordinator, process.Manifest, process.Revisions);
        var failed = await cleanup.CleanupBindingAsync();
        _output.WriteLine($"cleanup failed success={failed.Succeeded} failures={string.Join(",", failed.Failures.Select(f => f.Code))} " +
                          $"storeHas={(await process.Coordinator.GetByIdAsync(alarm.Id)) is not null} manifest={(await process.Manifest.LoadAsync()).Schedules.Count}");

        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Failures, f => f.Code == "AlarmCancelFailed");
        Assert.NotNull(await process.Coordinator.GetByIdAsync(alarm.Id));
        Assert.Single((await process.Manifest.LoadAsync()).Schedules);
        Assert.Equal(durableBefore, await process.Revisions.GetAppliedRevisionAsync(deviceId));

        // Retry with a working OS cancel completes the cleanup.
        os.FailOnCancelIds.Clear();
        var retry = await cleanup.CleanupBindingAsync();

        Assert.True(retry.Succeeded);
        Assert.Empty(retry.Failures);
        Assert.Null(await process.Coordinator.GetByIdAsync(alarm.Id));
        Assert.Empty((await process.Manifest.LoadAsync()).Schedules);
        Assert.Null(await process.Revisions.GetAppliedRevisionAsync(deviceId));
    }
}

/// <summary>
/// Applier-level regressions that need no API host: real JSON files + fake OS schedules.
/// </summary>
public sealed class T16ApplierRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t16applier-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly CloudSyncBinding _binding;
    private readonly FakeAlarmScheduler _os = new();
    private readonly AlarmCoordinator _coordinator;
    private readonly FileCloudSyncManifestStore _manifest;
    private readonly MobileDesiredApplier _applier;

    public T16ApplierRecoveryTests()
    {
        Directory.CreateDirectory(_dir);
        _binding = new CloudSyncBinding("https://api.example.test", "user-1", _deviceId, 1);
        var store = new JsonFileAlarmStore(Path.Combine(_dir, "alarms.json"));
        _coordinator = new AlarmCoordinator(store, new AlarmScheduleCalculator(new FakeHolidayService()), _os);
        _manifest = new FileCloudSyncManifestStore(_binding, Path.Combine(_dir, "manifest.json"));
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort test cleanup */ }
    }

    private static DesiredItemDto Item(Guid scheduleId, string time = "07:00", string repeat = "weekly", string? date = null, int[]? days = null)
        => new(
            ScheduleId: scheduleId,
            GroupId: "g1",
            Label: "S",
            GroupEnabled: true,
            Repeat: repeat,
            Days: days ?? new[] { 1, 2 },
            Date: date,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: false,
            ExcludedDates: Array.Empty<string>(),
            Time: time,
            SoundKey: "c",
            Volume: 50,
            Vibrate: true,
            Enabled: true,
            ModifiedRevision: 1);

    private static DesiredSnapshotDto Snapshot(Guid deviceId, long revision, IReadOnlyList<DesiredItemDto> items, IReadOnlyList<DesiredTombstoneDto>? tombstones = null)
        => new(deviceId, revision, Guid.NewGuid(), items, tombstones ?? Array.Empty<DesiredTombstoneDto>());

    [Fact]
    public async Task InvalidTime_IsRejectedBeforeAnySideEffect()
    {
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();

        var result = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(good), Item(bad, time: "25:99") }));

        Assert.False(result.Succeeded);
        Assert.True(result.Completed);
        Assert.Contains(result.Failures, f => f.ScheduleId == bad && f.Code == "InvalidItem");

        // No side effects at all: store, OS, manifest untouched (the valid item was not applied either).
        Assert.Empty(await _coordinator.GetAllAsync());
        Assert.Empty(_os.ScheduledAlarms);
        Assert.Empty((await _manifest.LoadAsync()).Schedules);
    }

    [Fact]
    public async Task InvalidOnceDateAndOutOfRangeDay_AreRejectedBeforeAnySideEffect()
    {
        var badDate = Guid.NewGuid();
        var badDay = Guid.NewGuid();

        var dateResult = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(badDate, repeat: "once", date: "2026-02-30") }));
        Assert.False(dateResult.Succeeded);
        Assert.Contains(dateResult.Failures, f => f.ScheduleId == badDate && f.Code == "InvalidItem");

        var dayResult = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(badDay, days: new[] { 1, 8 }) }));
        Assert.False(dayResult.Succeeded);
        Assert.Contains(dayResult.Failures, f => f.ScheduleId == badDay && f.Code == "InvalidItem");

        Assert.Empty(await _coordinator.GetAllAsync());
        Assert.Empty(_os.ScheduledAlarms);
        Assert.Empty((await _manifest.LoadAsync()).Schedules);
    }

    [Fact]
    public async Task DuplicateScheduleIds_AreRejectedBeforeAnySideEffect()
    {
        var scheduleId = Guid.NewGuid();

        var result = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(scheduleId), Item(scheduleId, time: "08:00") }));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Code == "DuplicateSchedule");
        Assert.Empty(await _coordinator.GetAllAsync());
        Assert.Empty(_os.ScheduledAlarms);
    }

    [Fact]
    public async Task StoreOnlyPrefixedAlarm_IsCollectedWithoutAnyManifestEntry()
    {
        // Simulates a process that died after store/OS side effects but before the manifest commit.
        var scheduleId = Guid.NewGuid();
        var orphan = new LocalAlarmModel
        {
            Id = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId),
            Name = "orphan",
            Time = new TimeOnly(7, 0),
            IsEnabled = true,
        };
        await _coordinator.SaveAndScheduleAsync(orphan);
        Assert.True(_os.ScheduledAlarms.ContainsKey(orphan.Id));

        // Server no longer lists the schedule and has no manifest knowledge of it.
        var result = await _applier.ApplyAsync(Snapshot(_deviceId, 2, Array.Empty<DesiredItemDto>(), new[] { new DesiredTombstoneDto(scheduleId) }));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Null(await _coordinator.GetByIdAsync(orphan.Id));
        Assert.False(_os.ScheduledAlarms.ContainsKey(orphan.Id));
    }

    [Fact]
    public async Task ManualAlarmWithoutPrefix_IsNeverCollected_EvenWhenDesiredIsEmpty()
    {
        var manual = new LocalAlarmModel { Id = "manual-1", Name = "manual", Time = new TimeOnly(5, 0), IsEnabled = true };
        await _coordinator.SaveAndScheduleAsync(manual);

        var result = await _applier.ApplyAsync(Snapshot(_deviceId, 1, Array.Empty<DesiredItemDto>()));

        Assert.True(result.Succeeded);
        Assert.NotNull(await _coordinator.GetByIdAsync("manual-1"));
        Assert.True(_os.ScheduledAlarms.ContainsKey("manual-1"));
    }

    [Fact]
    public async Task CancelFailureDuringCleanup_IsReportedAsFailure_AndKeepsStoreAndManifest()
    {
        var scheduleId = Guid.NewGuid();
        var first = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(scheduleId) }));
        Assert.True(first.Succeeded);

        var localId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
        Assert.NotNull(await _coordinator.GetByIdAsync(localId));

        _os.FailOnCancelIds.Add(localId);
        var result = await _applier.ApplyAsync(Snapshot(_deviceId, 2, Array.Empty<DesiredItemDto>(), new[] { new DesiredTombstoneDto(scheduleId) }));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Code == "AlarmCancelFailed" && f.ScheduleId == scheduleId);
        Assert.NotNull(await _coordinator.GetByIdAsync(localId));
        Assert.Single((await _manifest.LoadAsync()).Schedules);

        _os.FailOnCancelIds.Clear();
        var retried = await _applier.ApplyAsync(Snapshot(_deviceId, 2, Array.Empty<DesiredItemDto>(), new[] { new DesiredTombstoneDto(scheduleId) }));

        Assert.True(retried.Succeeded);
        Assert.Null(await _coordinator.GetByIdAsync(localId));
        Assert.Empty((await _manifest.LoadAsync()).Schedules);
    }

    [Fact]
    public async Task OsScheduleFailure_NeverWritesManifestEntry_SoNextCycleCanRecover()
    {
        var scheduleId = Guid.NewGuid();
        var localId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, scheduleId);
        _os.FailOnScheduleIds.Add(localId);

        var failed = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(scheduleId) }));
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Failures, f => f.Code == "OsRegistrationFailed");
        Assert.Empty((await _manifest.LoadAsync()).Schedules);

        _os.FailOnScheduleIds.Clear();
        var recovered = await _applier.ApplyAsync(Snapshot(_deviceId, 1, new[] { Item(scheduleId) }));
        Assert.True(recovered.Succeeded);
        Assert.True((await _manifest.LoadAsync()).Schedules.ContainsKey(scheduleId));
    }
}
