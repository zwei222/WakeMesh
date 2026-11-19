using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Sync.Tests.Fakes;
using WakeMesh.Mobile.Sync.Tests.TestDoubles;

namespace WakeMesh.Mobile.Sync.Tests.Integration;

/// <summary>
/// Vertical slice tests exercising full end-to-end cloud synchronization:
/// Device Registration -> AlarmGroup Creation -> Desired Fetch -> Local Apply (Store + OS) -> Applied Commit -> ACK.
/// Uses real in-process API (WebApplicationFactory + SQLite + JwtBearer signing)
/// and actual mobile services (JsonFileAlarmStore + AlarmCoordinator), paired with FakeAlarmScheduler.
///
/// IMPORTANT NOTE:
/// Success in these tests verifies the coordination and protocol flow against a fake OS double (FakeAlarmScheduler).
/// It does NOT constitute verification on real Android OS / AlarmManager execution.
/// Real Android execution must be tested on physical/emulated Android devices.
/// </summary>
public sealed class CloudSyncVerticalSliceTests : IClassFixture<SyncTestApiFactory>, IDisposable
{
    private readonly SyncTestApiFactory _factory;
    private readonly string _testDir;

    public CloudSyncVerticalSliceTests(SyncTestApiFactory factory)
    {
        _factory = factory;
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-vslice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task VerticalSlice_Register_Group_Desired_PersistAndSchedule_And_Ack_FullCycle()
    {
        // 1. Setup API Client with real TestServer transport and test JwtBearer auth
        var http = _factory.CreateClient();
        var tokens = new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken());
        var creds = new MemoryDeviceCredentialStore();
        var client = TestClientHelper.CreateApiClient(http, tokens, creds);

        // 2. Register device on API
        var reg = await client.RegisterDeviceAsync("Pixel-Slice-Device");
        Assert.True(reg.IsSuccess, $"Device registration failed: {reg.ErrorCode}");
        Assert.NotNull(reg.Value);
        var deviceId = reg.Value!.Id;

        // Verify credential stored
        var savedCred = await creds.GetCredentialAsync(deviceId);
        Assert.Equal(reg.Value.Credential, savedCred);

        // 3. Create real Mobile services for this device binding
        var binding = new CloudSyncBinding("https://api.wakemesh.local", "owner-user", deviceId, 1);
        var alarmFile = Path.Combine(_testDir, "alarms.json");
        var manifestFile = Path.Combine(_testDir, "manifest.json");
        var revFile = Path.Combine(_testDir, "rev.json");

        var alarmStore = new JsonFileAlarmStore(alarmFile);
        var fakeOsScheduler = new FakeAlarmScheduler(); // Note: Fake OS double; not real Android AlarmManager
        var holidayService = new FakeHolidayService();
        var calculator = new AlarmScheduleCalculator(holidayService);
        var coordinator = new AlarmCoordinator(alarmStore, calculator, fakeOsScheduler);

        var manifestStore = new FileCloudSyncManifestStore(binding, manifestFile);
        var applier = new MobileDesiredApplier(binding, coordinator, manifestStore);
        var appliedStore = new DurableAppliedRevisionStore(binding, revFile);

        var orchestrator = new DesiredSyncOrchestrator(client, applier, appliedStore);

        // 4. Create an alarm group on the server targeting this device
        var createResult = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Id: "weekday-morning",
            Label: "Weekday Morning Wakeup",
            Enabled: true,
            Repeat: "weekly",
            Days: new[] { 1, 2, 3, 4, 5 },
            Date: null,
            TimeZone: "Asia/Tokyo",
            ExcludeHolidays: true,
            ExcludedDates: Array.Empty<string>(),
            Targets: new[]
            {
                new AlarmTargetDto(
                    Id: null,
                    DeviceId: deviceId,
                    Time: "06:45",
                    SoundKey: "sunrise",
                    Volume: 85,
                    Vibrate: true,
                    Enabled: true)
            }));
        Assert.True(createResult.IsSuccess, $"Failed to create alarm group: {createResult.ErrorCode}");

        // 5. Run Desired Sync Cycle (GET desired -> Apply -> Commit -> ACK)
        var syncOutcome = await orchestrator.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, syncOutcome.Status);
        Assert.NotNull(syncOutcome.Snapshot);
        Assert.NotNull(syncOutcome.Ack);
        Assert.Equal(syncOutcome.Snapshot!.Revision, syncOutcome.Ack!.AppliedRevision);

        // 6. Verify local Mobile state
        // Alarms persisted in JsonFileAlarmStore
        var localAlarms = await coordinator.GetAllAsync();
        Assert.Single(localAlarms);
        var alarm = localAlarms[0];
        Assert.Equal("Weekday Morning Wakeup", alarm.Name);
        Assert.Equal(new TimeOnly(6, 45), alarm.Time);
        Assert.Equal(AlarmRegistrationStatus.Scheduled, alarm.OsRegistrationStatus);

        // Scheduled in Fake OS (verified explicitly as fake OS; not real Android OS)
        Assert.True(fakeOsScheduler.ScheduledAlarms.ContainsKey(alarm.Id));

        // Durable applied revision stored and committed
        var durableRev = await appliedStore.GetAppliedRevisionAsync(deviceId);
        Assert.Equal(syncOutcome.Snapshot.Revision, durableRev);

        // Manifest mapped
        var manifest = await manifestStore.LoadAsync();
        Assert.Single(manifest.Schedules);
        var targetScheduleId = syncOutcome.Snapshot.Items[0].ScheduleId;
        Assert.Equal(alarm.Id, manifest.Schedules[targetScheduleId].LocalAlarmId);

        // 7. Verify Server device details confirm AppliedRevision
        var devDetail = await client.GetDeviceAsync(deviceId);
        Assert.True(devDetail.IsSuccess);
        Assert.Equal(syncOutcome.Snapshot.Revision, devDetail.Value!.AppliedRevision);

        // 8. Delete group on server and sync again -> cancel and delete
        var deleteResult = await client.DeleteAlarmGroupAsync("weekday-morning", ifMatchVersion: createResult.Value!.Group.Version);
        Assert.True(deleteResult.IsSuccess);

        var cancelOutcome = await orchestrator.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, cancelOutcome.Status);

        // Local alarm removed and OS canceled
        var remainingAlarms = await coordinator.GetAllAsync();
        Assert.Empty(remainingAlarms);
        Assert.Empty(fakeOsScheduler.ScheduledAlarms);
        Assert.Contains(alarm.Id, fakeOsScheduler.CancelLog);

        // Revision moved forward on both local store and server
        var finalDurableRev = await appliedStore.GetAppliedRevisionAsync(deviceId);
        Assert.Equal(cancelOutcome.Snapshot!.Revision, finalDurableRev);
    }
}
