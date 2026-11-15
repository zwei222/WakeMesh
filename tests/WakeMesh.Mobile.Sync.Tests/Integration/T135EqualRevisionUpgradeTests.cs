using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
/// T135: an already-ACKed device whose on-disk payload predates <c>ExcludedDates</c> must be migrated exactly
/// once at an equal revision. Real in-process API + SQLite, real JSON stores, real
/// <see cref="DesiredSyncOrchestrator"/> and <see cref="DurableAppliedRevisionStore"/>; the OS is the
/// in-memory <see cref="FakeAlarmScheduler"/> (no native Android claim).
/// </summary>
public sealed class T135EqualRevisionUpgradeTests : IClassFixture<SyncTestApiFactory>, IDisposable
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private readonly SyncTestApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t135upgrade-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryDeviceCredentialStore _credentials = new();

    public T135EqualRevisionUpgradeTests(SyncTestApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort test cleanup */ }
    }

    private DateTimeOffset ToTokyo(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Tokyo);

    private DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToTokyo(instant).DateTime);

    private static string Wire(DateOnly date) => date.ToString("yyyy-MM-dd");

    private sealed class FailFirstAckHandler : DelegatingHandler
    {
        private int _remaining = 1;
        public int AckPosts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/acks", StringComparison.Ordinal) &&
                Interlocked.Decrement(ref _remaining) >= 0)
            {
                Interlocked.Increment(ref AckPosts);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Process
    {
        public required JsonFileAlarmStore Store;
        public required AlarmCoordinator Coordinator;
        public required ICloudSyncManifestStore Manifest;
        public required DurableAppliedRevisionStore Revisions;
        public required MobileDesiredApplier Applier;
        public required DesiredSyncOrchestrator Orchestrator;
    }

    private string AlarmPath => Path.Combine(_dir, "alarms.json");

    private Process NewProcess(WakeMeshApiClient client, Guid deviceId, IAlarmScheduler os)
    {
        var binding = new CloudSyncBinding("https://api.wakemesh.local", "owner-user", deviceId, 1);
        var store = new JsonFileAlarmStore(AlarmPath);
        var coordinator = new AlarmCoordinator(store, new AlarmScheduleCalculator(new FakeHolidayService()), os);
        var manifest = new FileCloudSyncManifestStore(binding, Path.Combine(_dir, $"manifest_{binding.ToStorageKey()}.json"));
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
        };
    }

    private WakeMeshApiClient NewApiClient(DelegatingHandler? handler = null)
        => TestClientHelper.CreateApiClient(
            handler is null ? _factory.CreateClient() : _factory.CreateDefaultClient(handler),
            new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()),
            _credentials);

    private async Task<(WakeMeshApiClient Client, Guid DeviceId)> RegisterAsync()
    {
        var client = NewApiClient();
        var registration = await client.RegisterDeviceAsync("t135-upgrade");
        Assert.True(registration.IsSuccess, registration.ErrorCode);
        return (client, registration.Value!.Id);
    }

    private static async Task<long> SaveGroupAsync(
        WakeMeshApiClient client,
        Guid deviceId,
        string groupId,
        IReadOnlyList<string> excludedDates,
        long? ifMatchVersion = null)
    {
        var request = new AlarmGroupWriteRequest(
            groupId,
            "T135 " + groupId,
            true,
            "weekly",
            new[] { 0, 1, 2, 3, 4, 5, 6 },
            null,
            "Asia/Tokyo",
            false,
            excludedDates,
            new[] { new AlarmTargetDto(null, deviceId, "07:00", "s", 80, true, true) });

        var result = ifMatchVersion is long version
            ? await client.ReplaceAlarmGroupAsync(groupId, request, version)
            : await client.CreateAlarmGroupAsync(request);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.Group.Version;
    }

    private static async Task SeedManualAsync(AlarmCoordinator coordinator)
        => await coordinator.SaveAndScheduleAsync(new LocalAlarmModel
        {
            Id = "manual-1",
            Name = "manual",
            IsEnabled = true,
            Time = new TimeOnly(5, 0),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday
                | Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday,
            TimeZoneId = "Asia/Tokyo",
        });

    private static async Task RemoveJsonKeyAsync(string path, string key)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))
                   ?? throw new InvalidOperationException($"{path} parsed as null");
        if (node is JsonArray array)
        {
            foreach (var element in array)
            {
                element!.AsObject().Remove(key);
            }
        }
        else
        {
            node.AsObject().Remove(key);
        }

        await File.WriteAllTextAsync(path, node.ToJsonString());
    }

    [Fact]
    public async Task LegacyAppliedDevice_ReappliesTheExcludedDatesOnceAtTheSameRevision_ThenNeverAgain()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var first = NewProcess(client, deviceId, os);
        await SeedManualAsync(first.Coordinator);

        // rev1: the group exists without excluded dates, so the next fire day D is known before the
        // server is told to exclude it (no fixed calendar date anywhere).
        var version1 = await SaveGroupAsync(client, deviceId, "t135", Array.Empty<string>());
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await first.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        var cloudAlarm = (await first.Coordinator.GetAllAsync()).Single(a => a.Id != "manual-1");
        Assert.NotNull(cloudAlarm.NextFireTime);
        var excludedDay = LocalDate(cloudAlarm.NextFireTime!.Value);

        // rev2: the exclusion arrives through the normal apply path, so this device is already ACKed for it.
        var version2 = await SaveGroupAsync(client, deviceId, "t135", new[] { Wire(excludedDay) }, version1);
        Assert.True(version2 > version1);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await first.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        var appliedOnce = await first.Coordinator.GetByIdAsync(cloudAlarm.Id);
        Assert.Equal(new[] { excludedDay }, appliedOnce!.ExcludedDates);
        var appliedRevision = await first.Revisions.GetAppliedRevisionAsync(deviceId);
        Assert.NotNull(appliedRevision);
        Assert.Equal(1, (await first.Manifest.LoadAsync()).AppliedItemFormat);

        // Simulate the on-disk state left by the pre-T135 build: no ExcludedDates on the alarm, no marker in
        // the manifest, and the OS reservation still pointing at D. The applied revision is untouched, so the
        // next sync arrives at exactly the committed revision.
        await RemoveJsonKeyAsync(AlarmPath, nameof(LocalAlarmModel.ExcludedDates));
        await RemoveJsonKeyAsync(
            Path.Combine(_dir, $"manifest_{new CloudSyncBinding("https://api.wakemesh.local", "owner-user", deviceId, 1).ToStorageKey()}.json"),
            nameof(CloudSyncManifest.AppliedItemFormat));

        var legacyStore = new JsonFileAlarmStore(AlarmPath);
        var legacyAlarm = await legacyStore.GetByIdAsync(cloudAlarm.Id);
        Assert.Empty(legacyAlarm!.ExcludedDates);
        // The pre-T135 build had no exclusion, so the OS reservation it left points at D (same local time,
        // one day earlier than the value this build computed for the exclusion).
        os.ScheduledAlarms[cloudAlarm.Id] = new FakeAlarmScheduler.ScheduledEntry(
            legacyAlarm, legacyAlarm.NextFireTime!.Value.AddDays(-1));
        Assert.Equal(excludedDay, LocalDate(os.ScheduledAlarms[cloudAlarm.Id].FireTime));

        var scheduleCallsBefore = os.ScheduleCalls;
        var third = NewProcess(client, deviceId, os);
        Assert.True(await third.Applier.RequiresReapplyAtEqualRevisionAsync());

        var outcome = await third.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"T135 upgrade status={outcome.Status} applies={os.ScheduleCalls - scheduleCallsBefore} " +
                          $"durable={await third.Revisions.GetAppliedRevisionAsync(deviceId)} server={appliedRevision}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, outcome.Status);
        Assert.Equal(scheduleCallsBefore + 1, os.ScheduleCalls);   // exactly one re-apply

        var restored = await third.Coordinator.GetByIdAsync(cloudAlarm.Id);
        Assert.NotNull(restored);
        Assert.Equal(cloudAlarm.Id, restored!.Id);                 // same local id, no duplicate
        Assert.Equal(new[] { excludedDay }, restored.ExcludedDates);
        Assert.Equal(excludedDay.AddDays(1), LocalDate(restored.NextFireTime!.Value));
        Assert.Equal(excludedDay.AddDays(1), LocalDate(os.ScheduledAlarms[restored.Id].FireTime));
        Assert.Equal(1, (await third.Manifest.LoadAsync()).AppliedItemFormat);

        // The applied revision is unchanged (equal, not advanced) and the server saw the success ACK.
        Assert.Equal(appliedRevision, await third.Revisions.GetAppliedRevisionAsync(deviceId));
        var device = await client.GetDeviceAsync(deviceId);
        Assert.Equal(device.Value!.DesiredRevision, device.Value.AppliedRevision);

        // Manual alarms and their OS registration are untouched by the migration.
        Assert.NotNull(await third.Coordinator.GetByIdAsync("manual-1"));
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));

        // Sync again: the marker is 1, so the equal revision is re-ACKed without another OS apply.
        var callsBeforeReAck = os.ScheduleCalls;
        var fourth = await third.Orchestrator.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, fourth.Status);
        Assert.Equal(callsBeforeReAck, os.ScheduleCalls);
    }

    [Fact]
    public async Task FailedAckAfterTheMigration_KeepsTheMarkerAndNeverReappliesOnTheRetry()
    {
        var (client, deviceId) = await RegisterAsync();
        var os = new FakeAlarmScheduler();
        var first = NewProcess(client, deviceId, os);
        await SeedManualAsync(first.Coordinator);

        var version = await SaveGroupAsync(client, deviceId, "t135b", Array.Empty<string>());
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await first.Orchestrator.SyncDeviceAsync(deviceId)).Status);
        var cloudAlarm = (await first.Coordinator.GetAllAsync()).Single(a => a.Id != "manual-1");
        var excludedDay = LocalDate(cloudAlarm.NextFireTime!.Value);
        await SaveGroupAsync(client, deviceId, "t135b", new[] { Wire(excludedDay) }, version);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await first.Orchestrator.SyncDeviceAsync(deviceId)).Status);

        // Force the pre-T135 shape again so the migration runs in this test too.
        await RemoveJsonKeyAsync(AlarmPath, nameof(LocalAlarmModel.ExcludedDates));
        await RemoveJsonKeyAsync(
            Path.Combine(_dir, $"manifest_{new CloudSyncBinding("https://api.wakemesh.local", "owner-user", deviceId, 1).ToStorageKey()}.json"),
            nameof(CloudSyncManifest.AppliedItemFormat));

        // The migration apply succeeds but the success ACK is dropped.
        var dropAck = new FailFirstAckHandler();
        var lossyClient = NewApiClient(dropAck);
        var lossy = NewProcess(lossyClient, deviceId, os);
        var appliedCallsBefore = os.ScheduleCalls;

        var lost = await lossy.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"T135 ack-loss status={lost.Status} applies={os.ScheduleCalls - appliedCallsBefore} " +
                          $"marker={(await lossy.Manifest.LoadAsync()).AppliedItemFormat}");

        Assert.Equal(DesiredSyncStatus.TransportFailed, lost.Status);
        Assert.Equal(appliedCallsBefore + 1, os.ScheduleCalls);   // the re-apply itself happened
        Assert.Equal(1, (await lossy.Manifest.LoadAsync()).AppliedItemFormat);   // stamped before the ACK

        // Retry with a healthy client: equal revision + marker 1 → re-ACK only, no second OS apply.
        var retry = NewProcess(client, deviceId, os);
        var callsBeforeRetry = os.ScheduleCalls;
        var reAck = await retry.Orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"T135 ack-loss retry status={reAck.Status} applies={os.ScheduleCalls - callsBeforeRetry}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, reAck.Status);
        Assert.Equal(callsBeforeRetry, os.ScheduleCalls);
        var stored = await retry.Coordinator.GetByIdAsync(cloudAlarm.Id);
        Assert.Equal(new[] { excludedDay }, stored!.ExcludedDates);
        Assert.True(os.ScheduledAlarms.ContainsKey("manual-1"));
    }
}
