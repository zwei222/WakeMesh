using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// Vertical slice tests exercising full end-to-end cloud management & synchronization:
/// UI Commands -> CloudManagementService -> Real API (SQLite+JWT) -> MobileDesiredApplier -> FakeAlarmScheduler -> ACK.
/// Verifies that no fictive automatic registrations occur and UI commands drive the real pipeline.
/// </summary>
public sealed class CloudManagementVerticalSliceTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private readonly CloudUiTestApiFactory _factory;
    private readonly string _testDir;

    public CloudManagementVerticalSliceTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-cloudui-" + Guid.NewGuid().ToString("N"));
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
    public async Task VerticalSlice_Register_List_GroupEdit_ManualSync_Ack_FullCycle()
    {
        // 1. Arrange Real in-process HTTP client and real JWT auth token
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        // 2. Setup real storage & service dependencies in isolated test directory
        var alarmsFile = Path.Combine(_testDir, "alarms.json");
        var regFile = Path.Combine(_testDir, "registration.json");
        var secureStorage = new StubSecureStorageProvider();
        var credStore = new SecureDeviceCredentialStore(secureStorage, () => mockAuth.CurrentBinding);
        var regStore = new FileCloudDeviceRegistrationStore(regFile);

        var alarmStore = new JsonFileAlarmStore(alarmsFile);
        var fakeScheduler = new FakeAlarmScheduler();
        var fakeHoliday = new FakeHolidayService();
        var calculator = new AlarmScheduleCalculator(fakeHoliday);
        var coordinator = new AlarmCoordinator(alarmStore, calculator, fakeScheduler);

        // WakeMeshApiClient factory injecting the TestServer transport
        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, mockAuth, credStore);

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            credStore,
            coordinator,
            ClientFactory,
            customStorageDir: _testDir);

        string? navigatedRoute = null;
        var vm = new CloudManagementViewModel(cloudService, mockAuth, route => navigatedRoute = route);

        // 3. Step 1: Initial state - verify unregistered
        await vm.InitializeAsync();
        Assert.False(vm.IsRegistered);
        Assert.Null(vm.CurrentServerDeviceId);

        // 4. Step 2: UI Command - Register current device
        vm.NewDeviceDisplayName = "Pixel-Tablet-Live";
        await vm.RegisterCurrentDeviceCommand.ExecuteAsync(null);

        // Assert registration succeeded and state updated
        Assert.True(vm.IsRegistered);
        Assert.NotNull(vm.CurrentServerDeviceId);
        var currentDeviceId = vm.CurrentServerDeviceId.Value;
        Assert.NotEqual(Guid.Empty, currentDeviceId);

        // Verify credentials persisted securely
        var savedCred = await credStore.GetCredentialAsync(currentDeviceId);
        Assert.False(string.IsNullOrWhiteSpace(savedCred));

        // Verify registration persisted in local no-backup store
        var savedReg = await regStore.LoadRegistrationAsync();
        Assert.NotNull(savedReg);
        Assert.Equal(currentDeviceId, savedReg!.ServerDeviceId);
        Assert.Equal(1, savedReg.RegistrationGeneration);

        // 5. Step 3: UI Command - LoadCloudData (lists devices and alarm groups from real API)
        await vm.LoadDataCommand.ExecuteAsync(null);
        Assert.False(vm.IsDeviceListError);
        Assert.Contains(vm.Devices, d => d.Id == currentDeviceId && d.IsCurrentDevice);

        // 6. Step 4: UI Command - Create Alarm Group via CloudAlarmGroupEditViewModel
        var groupVm = new CloudAlarmGroupEditViewModel(cloudService, route => navigatedRoute = route);
        await groupVm.InitializeAsync();

        groupVm.Label = "朝のモーニングコール";
        groupVm.DayMonday = true;
        groupVm.DayTuesday = true;
        groupVm.DayWednesday = true;
        groupVm.DayThursday = true;
        groupVm.DayFriday = true;
        groupVm.DaySaturday = false;
        groupVm.DaySunday = false;
        groupVm.ExcludeHolidays = true;

        // Ensure target is set for current device
        groupVm.Targets.Clear();
        groupVm.Targets.Add(new CloudAlarmTargetItemViewModel(
            currentDeviceId,
            "Pixel-Tablet-Live",
            new TimeSpan(6, 45, 0),
            "sunrise",
            85,
            true,
            true));

        await groupVm.SaveCommand.ExecuteAsync(null);
        Assert.Null(groupVm.ErrorMessage);
        Assert.False(groupVm.IsNew);
        Assert.True(groupVm.IfMatchVersion > 0);
        Assert.NotNull(groupVm.SaveOutcomeNotice);
        Assert.Contains("サーバー保存成功", groupVm.SaveOutcomeNotice);

        // 7. Step 5: UI Command - Manual Sync (SyncCurrentDeviceAsync -> DesiredSyncOrchestrator -> MobileDesiredApplier -> ACK)
        await vm.ManualSyncCommand.ExecuteAsync(null);

        Assert.Contains("同期完了", vm.LastSyncStatusMessage);
        Assert.Contains("ACK送信済", vm.LastSyncStatusMessage);

        // 8. Step 6: Verify local alarms & Fake OS execution
        var allLocalAlarms = await coordinator.GetAllAsync();
        Assert.Single(allLocalAlarms);
        var appliedAlarm = allLocalAlarms[0];
        Assert.Equal("朝のモーニングコール", appliedAlarm.Name);
        Assert.Equal(new TimeOnly(6, 45), appliedAlarm.Time);
        Assert.Equal(AlarmRegistrationStatus.Scheduled, appliedAlarm.OsRegistrationStatus);

        // Verified on Fake OS (explicit double, not real Android AlarmManager)
        Assert.True(fakeScheduler.ScheduledAlarms.ContainsKey(appliedAlarm.Id));

        // 9. Step 7: Verify Server confirmed ACK and AppliedRevision matches DesiredRevision
        var client = ClientFactory();
        var devDetail = await client.GetDeviceAsync(currentDeviceId);
        Assert.True(devDetail.IsSuccess);
        Assert.Equal(devDetail.Value!.DesiredRevision, devDetail.Value!.AppliedRevision);

        // Verify UI reflections show applied status
        await vm.LoadDataCommand.ExecuteAsync(null);
        var currentDevItem = vm.Devices.First(d => d.Id == currentDeviceId);
        Assert.Contains("適用報告済み (ACK済", currentDevItem.SyncStatusText);

        // 10. Step 8: ETag 412 Conflict handling
        // Modify group on server directly behind the edit VM's back
        var directClient = ClientFactory();
        var directModify = await directClient.ReplaceAlarmGroupAsync(
            groupVm.GroupId,
            new AlarmGroupWriteRequest(
                groupVm.GroupId,
                "競合更新テスト",
                true,
                "weekly",
                new[] { 1, 2, 3 },
                null,
                "Asia/Tokyo",
                true,
                Array.Empty<string>(),
                new[]
                {
                    new AlarmTargetDto(null, currentDeviceId, "07:00", "default", 80, true, true)
                }),
            ifMatchVersion: groupVm.IfMatchVersion);
        Assert.True(directModify.IsSuccess);

        // Attempting to save with old IfMatchVersion in groupVm triggers ETag 412
        groupVm.Label = "ローカルで変更したままのラベル";
        await groupVm.SaveCommand.ExecuteAsync(null);

        // Conflict detected, user input preserved!
        Assert.True(groupVm.ConflictDetected);
        Assert.NotNull(groupVm.ConflictMessage);
        Assert.Contains("ETag 412", groupVm.ConflictMessage);
        Assert.Equal("ローカルで変更したままのラベル", groupVm.Label); // Kept intact!

        // Reload from server resolves conflict
        await groupVm.ReloadFromServerCommand.ExecuteAsync(null);
        Assert.False(groupVm.ConflictDetected);
        Assert.Equal("競合更新テスト", groupVm.Label);
        Assert.Equal(directModify.Value!.Group.Version, groupVm.IfMatchVersion);
    }

    [Fact]
    public async Task Unconfigured_Or_SignedOut_BlocksApiAndRegistration_GuidesToSettings()
    {
        var http = _factory.CreateClient();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", "token");
        mockAuth.SetState(AuthState.SignedOut, "サインアウト中");

        var regFile = Path.Combine(_testDir, "unconf_reg.json");
        var alarmsFile = Path.Combine(_testDir, "unconf_alarms.json");
        var regStore = new FileCloudDeviceRegistrationStore(regFile);
        var alarmStore = new JsonFileAlarmStore(alarmsFile);
        var coordinator = new AlarmCoordinator(alarmStore, new AlarmScheduleCalculator(new FakeHolidayService()), new FakeAlarmScheduler());

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            null,
            coordinator,
            () => TestClientHelper.CreateApiClient(http, mockAuth));

        string? navigatedRoute = null;
        var vm = new CloudManagementViewModel(cloudService, mockAuth, route => navigatedRoute = route);
        await vm.InitializeAsync();

        Assert.False(vm.IsConfiguredAndSignedIn);
        Assert.False(vm.RegisterCurrentDeviceCommand.CanExecute(null));
        Assert.Contains("サインイン", vm.AuthStateText);

        // ExecuteAsync does nothing because CanExecute is false
        await vm.RegisterCurrentDeviceCommand.ExecuteAsync(null);

        // Navigate to settings works
        vm.NavigateToSettingsCommand.Execute(null);
        Assert.Equal("SettingsPage", navigatedRoute);
    }

    [Fact]
    public async Task UnknownOutcome_RecoveryNeeded_BlocksSpamReRegistration()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        var regFile = Path.Combine(_testDir, "rec_reg.json");
        var regStore = new FileCloudDeviceRegistrationStore(regFile);

        // Pre-seed registration store with recoveryNeeded = true
        var problemReg = new CloudDeviceRegistration(
            Guid.NewGuid(),
            1,
            "https://api.wakemesh.local",
            "test-owner-account-id",
            "Broken-Device",
            recoveryNeeded: true,
            recoveryReason: "network timed out during credential persistence");
        await regStore.SaveRegistrationAsync(problemReg);

        var alarmStore = new JsonFileAlarmStore(Path.Combine(_testDir, "rec_alarms.json"));
        var coordinator = new AlarmCoordinator(alarmStore, new AlarmScheduleCalculator(new FakeHolidayService()), new FakeAlarmScheduler());

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            null,
            coordinator,
            () => TestClientHelper.CreateApiClient(http, mockAuth));

        var vm = new CloudManagementViewModel(cloudService, mockAuth);
        await vm.InitializeAsync();

        Assert.True(vm.IsRecoveryNeeded);
        Assert.False(vm.RegisterCurrentDeviceCommand.CanExecute(null));

        // Clear recovery command unblocks
        await vm.ClearRecoveryCommand.ExecuteAsync(null);
        Assert.False(vm.IsRecoveryNeeded);
        Assert.True(vm.RegisterCurrentDeviceCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsChange_CancelsActiveSync_PreventsMismatchedScopeAck()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        var regFile = Path.Combine(_testDir, "scope_reg.json");
        var regStore = new FileCloudDeviceRegistrationStore(regFile);

        // Register with original url
        var deviceId = Guid.NewGuid();
        var originalReg = new CloudDeviceRegistration(
            deviceId,
            1,
            "https://api.wakemesh.local",
            "test-owner-account-id",
            "Scope-Device");
        await regStore.SaveRegistrationAsync(originalReg);

        var alarmStore = new JsonFileAlarmStore(Path.Combine(_testDir, "scope_alarms.json"));
        var coordinator = new AlarmCoordinator(alarmStore, new AlarmScheduleCalculator(new FakeHolidayService()), new FakeAlarmScheduler());

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            null,
            coordinator,
            () => TestClientHelper.CreateApiClient(http, mockAuth));

        // Change settings to a different server URL
        mockAuth.SetSettings(new AuthSettings
        {
            ApiBaseUrl = "https://other-server.wakemesh.local",
            AndroidClientId = Guid.NewGuid().ToString("D"),
            ApiClientId = Guid.NewGuid().ToString("D")
        });

        // SyncCurrentDeviceAsync must refuse to send ACK or secrets to mismatched scope
        var outcome = await cloudService.SyncCurrentDeviceAsync();
        Assert.Equal(DesiredSyncStatus.CanceledNoAck, outcome.Status);
        Assert.Equal("scopeMismatch", outcome.ErrorCode);
    }

    [Fact]
    public async Task ManualLocalAlarm_IsStrictlyPreserved_WhenCloudSyncApplies()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        var alarmsFile = Path.Combine(_testDir, "pres_alarms.json");
        var regFile = Path.Combine(_testDir, "pres_reg.json");
        var credStore = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => mockAuth.CurrentBinding);
        var regStore = new FileCloudDeviceRegistrationStore(regFile);

        var alarmStore = new JsonFileAlarmStore(alarmsFile);
        var fakeScheduler = new FakeAlarmScheduler();
        var coordinator = new AlarmCoordinator(alarmStore, new AlarmScheduleCalculator(new FakeHolidayService()), fakeScheduler);

        // Pre-create a manual local alarm that user created before cloud registration
        var manualAlarm = new LocalAlarmModel
        {
            Id = "manual-local-alarm-1",
            Name = "純粋な手動ローカルアラーム",
            Time = new TimeOnly(8, 0),
            IsEnabled = true,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Friday
        };
        await coordinator.SaveAndScheduleAsync(manualAlarm);

        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, mockAuth, credStore);

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            credStore,
            coordinator,
            ClientFactory,
            customStorageDir: _testDir);

        var vm = new CloudManagementViewModel(cloudService, mockAuth);
        await vm.InitializeAsync();

        // Register device
        vm.NewDeviceDisplayName = "Device-With-Local-Alarm";
        await vm.RegisterCurrentDeviceCommand.ExecuteAsync(null);

        // Create a cloud alarm group
        var groupVm = new CloudAlarmGroupEditViewModel(cloudService);
        await groupVm.InitializeAsync();
        groupVm.Label = "クラウド側アラーム";
        groupVm.Targets.Clear();
        groupVm.Targets.Add(new CloudAlarmTargetItemViewModel(
            vm.CurrentServerDeviceId!.Value,
            "Device-With-Local-Alarm",
            new TimeSpan(9, 30, 0),
            "sound",
            90,
            true,
            false));
        await groupVm.SaveCommand.ExecuteAsync(null);

        // Manual sync
        await vm.ManualSyncCommand.ExecuteAsync(null);
        Assert.Contains("同期完了", vm.LastSyncStatusMessage);

        // Check local alarms: BOTH manual local alarm AND cloud alarm must be present!
        var allAlarms = await coordinator.GetAllAsync();
        Assert.Equal(2, allAlarms.Count);
        Assert.Contains(allAlarms, a => a.Id == "manual-local-alarm-1" && a.Name == "純粋な手動ローカルアラーム");
        Assert.Contains(allAlarms, a => a.Name == "クラウド側アラーム");
    }

    [Fact]
    public async Task AlarmGroup_Delete_And_Device_Retire_Revoke_UICommands_SyncWithApi()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        var regFile = Path.Combine(_testDir, "mgmt_reg.json");
        var regStore = new FileCloudDeviceRegistrationStore(regFile);
        var credStore = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => mockAuth.CurrentBinding);

        var alarmStore = new JsonFileAlarmStore(Path.Combine(_testDir, "mgmt_alarms.json"));
        var coordinator = new AlarmCoordinator(alarmStore, new AlarmScheduleCalculator(new FakeHolidayService()), new FakeAlarmScheduler());

        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, mockAuth, credStore);

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            credStore,
            coordinator,
            ClientFactory,
            customStorageDir: _testDir);

        var vm = new CloudManagementViewModel(cloudService, mockAuth);
        await vm.InitializeAsync();

        // Register device 1
        vm.NewDeviceDisplayName = "Primary-Device";
        await vm.RegisterCurrentDeviceCommand.ExecuteAsync(null);
        var dev1Id = vm.CurrentServerDeviceId!.Value;

        // Register device 2 directly via API
        var client = ClientFactory();
        var dev2Reg = await client.RegisterDeviceAsync("Secondary-Device", persistCredential: false);
        Assert.True(dev2Reg.IsSuccess);
        var dev2Id = dev2Reg.Value!.Id;

        // Create an alarm group
        var groupVm = new CloudAlarmGroupEditViewModel(cloudService);
        await groupVm.InitializeAsync();
        groupVm.Label = "削除対象グループ";
        await groupVm.SaveCommand.ExecuteAsync(null);
        var groupId = groupVm.GroupId;

        // Refresh main VM data
        await vm.LoadDataCommand.ExecuteAsync(null);
        Assert.Contains(vm.Devices, d => d.Id == dev2Id);
        var groupItem = vm.AlarmGroups.FirstOrDefault(g => g.Id == groupId);
        Assert.NotNull(groupItem);

        // 1. Delete group via UI command
        await vm.DeleteGroupCommand.ExecuteAsync(groupItem);
        Assert.DoesNotContain(vm.AlarmGroups, g => g.Id == groupId);

        // Verify on server
        var groupsOnServer = await client.ListAlarmGroupsAsync();
        Assert.True(groupsOnServer.IsSuccess);
        Assert.DoesNotContain(groupsOnServer.Value!, g => g.Id == groupId);

        // 2. Retire device 2 via UI command
        await vm.RetireDeviceCommand.ExecuteAsync(dev2Id);
        var retiredItem = vm.Devices.First(d => d.Id == dev2Id);
        Assert.True(retiredItem.IsRetired);
        Assert.Contains("退役", retiredItem.SyncStatusText);

        // 3. Revoke device 2 via UI command
        await vm.RevokeDeviceCommand.ExecuteAsync(dev2Id);
        var revokedItem = vm.Devices.First(d => d.Id == dev2Id);
        Assert.True(revokedItem.IsRevoked);
        Assert.Contains("取消", revokedItem.SyncStatusText);
    }

    [Fact]
    public async Task ApiError_DistinguishedFrom_EmptyList()
    {
        // Setup handler that returns 503 Service Unavailable
        var mockAuth = new MockAuthService("https://broken.wakemesh.local", "token");
        var failingHttp = new System.Net.Http.HttpClient(new FailingHttpMessageHandler())
        {
            BaseAddress = new Uri("https://broken.wakemesh.local")
        };

        var regFile = Path.Combine(_testDir, "err_reg.json");
        var regStore = new FileCloudDeviceRegistrationStore(regFile);
        var credStore = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => mockAuth.CurrentBinding);
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(_testDir, "err_alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            credStore,
            coordinator,
            () => TestClientHelper.CreateApiClient(failingHttp, mockAuth, credStore),
            customStorageDir: _testDir);

        var vm = new CloudManagementViewModel(cloudService, mockAuth);
        await vm.InitializeAsync();

        await vm.LoadDataCommand.ExecuteAsync(null);

        // Offline / server error must NOT be presented as empty lists without error flag
        Assert.True(vm.IsDeviceListError);
        Assert.NotNull(vm.DeviceListErrorMessage);
        Assert.True(vm.IsGroupListError);
        Assert.NotNull(vm.GroupListErrorMessage);
    }

    private sealed class FailingHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new System.Net.Http.StringContent("{\"error\":\"server_down\"}")
            });
        }
    }
}
