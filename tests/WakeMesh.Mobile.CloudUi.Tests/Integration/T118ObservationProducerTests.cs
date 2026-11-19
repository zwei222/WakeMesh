using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// T118 producer test: the real <see cref="CloudManagementService.SyncCurrentDeviceAsync"/> cycle against the
/// in-process API must hand its really-received desired snapshot to <see cref="DesiredObservationStore"/>,
/// keyed by the binding it was fetched with — and must forget it when the identity changes.
/// </summary>
public sealed class T118ObservationProducerTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private readonly CloudUiTestApiFactory _factory;
    private readonly string _testDir;

    public T118ObservationProducerTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-t118-producer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    private sealed record Harness(
        CloudManagementService Service,
        DesiredObservationStore Observations,
        MockAuthService Auth,
        FileCloudDeviceRegistrationStore Registrations);

    private Harness NewHarness()
    {
        var http = _factory.CreateClient();
        var auth = new MockAuthService("https://api.wakemesh.local", _factory.Auth.CreateAccessToken());
        var secureStorage = new StubSecureStorageProvider();
        var credentials = new SecureDeviceCredentialStore(secureStorage, () => auth.CurrentBinding);

        var registrationStore = new FileCloudDeviceRegistrationStore(Path.Combine(_testDir, "registration.json"));
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(_testDir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

        var observations = new DesiredObservationStore();
        var service = new CloudManagementService(
            auth,
            registrationStore,
            credentials,
            coordinator,
            () => TestClientHelper.CreateApiClient(http, auth, credentials),
            customStorageDir: _testDir,
            observations: observations);

        return new Harness(service, observations, auth, registrationStore);
    }

    [Fact]
    public async Task RealSyncCycle_PublishesTheReceivedSnapshotUnderTheRegistrationBinding()
    {
        var harness = NewHarness();

        // Register this device through the real service, then create one group that targets it.
        var registration = await harness.Service.RegisterCurrentDeviceAsync("T118 Fold5");
        Assert.True(registration.IsSuccess, registration.ErrorCode ?? registration.Message);
        var deviceId = registration.Value!.Id;

        var http = _factory.CreateClient();
        var client = TestClientHelper.CreateApiClient(
            http, harness.Auth, new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => harness.Auth.CurrentBinding));

        var group = await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Guid.NewGuid().ToString("D"),
            "T118 モーニング",
            true,
            "weekly",
            new[] { 1, 2, 3, 4, 5 },
            null,
            "Asia/Tokyo",
            true,
            Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, deviceId, "06:45", "default", 80, true, true) }));
        Assert.True(group.IsSuccess, group.ErrorCode ?? group.Message);

        Assert.Null(harness.Observations.Latest);

        var outcome = await harness.Service.SyncCurrentDeviceAsync();

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, outcome.Status);
        Assert.NotNull(outcome.Snapshot);

        var saved = await harness.Registrations.LoadRegistrationAsync();
        Assert.NotNull(saved);
        var expectedKey = new CloudSyncBinding(
            saved!.ApiBaseUrl, saved.AccountId, saved.ServerDeviceId, saved.RegistrationGeneration).ToStorageKey();

        var latest = harness.Observations.Latest;
        Assert.NotNull(latest);
        Assert.Equal(expectedKey, latest!.BindingKey);
        Assert.Equal(outcome.Snapshot!.Revision, latest.Snapshot.Revision);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, latest.Status);
        Assert.Single(latest.Snapshot.Items);
        Assert.Equal("T118 モーニング", latest.Snapshot.Items[0].Label);
    }

    [Fact]
    public async Task SignOut_ClearsTheObservation_SoNothingIsClaimedForTheNewIdentity()
    {
        var harness = NewHarness();

        var registration = await harness.Service.RegisterCurrentDeviceAsync("T118 Fold5");
        Assert.True(registration.IsSuccess);
        var deviceId = registration.Value!.Id;

        var http = _factory.CreateClient();
        var client = TestClientHelper.CreateApiClient(
            http, harness.Auth, new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => harness.Auth.CurrentBinding));
        await client.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            Guid.NewGuid().ToString("D"), "T118 モーニング", true, "weekly", new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo", true,
            Array.Empty<string>(), new[] { new AlarmTargetDto(null, deviceId, "06:45", "default", 80, true, true) }));

        await harness.Service.SyncCurrentDeviceAsync();
        Assert.NotNull(harness.Observations.Latest);

        harness.Auth.SetState(AuthState.SignedOut, "サインアウト");

        Assert.Null(harness.Observations.Latest);

        // A signed-out cycle publishes nothing either.
        var afterSignOut = await harness.Service.SyncCurrentDeviceAsync();
        Assert.Equal(DesiredSyncStatus.CanceledNoAck, afterSignOut.Status);
        Assert.Null(harness.Observations.Latest);
    }

    [Fact]
    public async Task FailedTransport_PublishesNothing()
    {
        var harness = NewHarness();

        var registration = await harness.Service.RegisterCurrentDeviceAsync("T118 Fold5");
        Assert.True(registration.IsSuccess);

        // Same service, but the client points at a dead origin: the cycle fails before any snapshot exists.
        var broken = new CloudManagementService(
            harness.Auth,
            harness.Registrations,
            null,
            new AlarmCoordinator(
                new JsonFileAlarmStore(Path.Combine(_testDir, "alarms-broken.json")),
                new AlarmScheduleCalculator(new FakeHolidayService()),
                new FakeAlarmScheduler()),
            () => WakeMeshApiClient.Create("https://127.0.0.1:9", harness.Auth, null),
            customStorageDir: _testDir,
            observations: harness.Observations);

        var outcome = await broken.SyncCurrentDeviceAsync();

        Assert.Equal(DesiredSyncStatus.TransportFailed, outcome.Status);
        Assert.Null(harness.Observations.Latest);
    }
}
