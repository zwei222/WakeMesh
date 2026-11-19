using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Core;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// T176 — the production cloud group-edit flow (real <see cref="WakeMeshApiClient"/>, real
/// <see cref="CloudManagementService"/>, production <see cref="CloudAlarmGroupEditViewModel"/> +
/// <c>SaveCommand</c>) must save an added member without reporting a conflict.
///
/// Before the server fix, replacing a group with a device that was not yet a member returned
/// 412 <c>concurrencyConflict</c>, which the view model surfaced as "他の場所で更新されています"
/// even though nothing had actually changed elsewhere.
/// </summary>
public sealed class T176MemberAddVerticalTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private readonly CloudUiTestApiFactory _factory;
    private readonly string _testDir;

    public T176MemberAddVerticalTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-t176-" + Guid.NewGuid().ToString("N"));
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
    public async Task T176_GroupEdit_AddMember_SaveCommand_Succeeds_WithoutConflict_AndKeepsDraft()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        var credStore = new SecureDeviceCredentialStore(
            new StubSecureStorageProvider(), () => mockAuth.CurrentBinding);
        var regStore = new FileCloudDeviceRegistrationStore(Path.Combine(_testDir, "reg.json"));
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(_testDir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, mockAuth, credStore);

        var cloudService = new CloudManagementService(
            mockAuth, regStore, credStore, coordinator, ClientFactory, customStorageDir: _testDir);

        // --- register this device, then a second device that will be added to the group ---
        var shell = new CloudManagementViewModel(cloudService, mockAuth);
        await shell.InitializeAsync();
        shell.NewDeviceDisplayName = "T176-Primary";
        await shell.RegisterCurrentDeviceCommand.ExecuteAsync(null);
        var primaryId = shell.CurrentServerDeviceId!.Value;

        var client = ClientFactory();
        var secondaryReg = await client.RegisterDeviceAsync("T176-Secondary", persistCredential: false);
        Assert.True(secondaryReg.IsSuccess);
        var secondaryId = secondaryReg.Value!.Id;

        // --- create the group with only the primary device (this path already worked) ---
        var createVm = new CloudAlarmGroupEditViewModel(cloudService);
        await createVm.InitializeAsync();
        createVm.Label = "T176 メンバー追加";
        await createVm.SaveCommand.ExecuteAsync(null);
        Assert.Null(createVm.ErrorMessage);
        Assert.False(createVm.ConflictDetected);
        Assert.Equal(1L, createVm.IfMatchVersion);
        Assert.Equal(1, createVm.Targets.Count);
        var groupId = createVm.GroupId;

        // --- reopen the saved group and add the second device through the real commands ---
        var editVm = new CloudAlarmGroupEditViewModel(cloudService);
        await editVm.InitializeAsync(groupId);
        Assert.Null(editVm.ErrorMessage);
        Assert.False(editVm.IsNew);
        Assert.Equal(1L, editVm.IfMatchVersion);
        Assert.Single(editVm.Targets);
        Assert.Contains(editVm.AvailableDevices, d => d.Id == secondaryId);

        editVm.SelectedDeviceToAdd = editVm.AvailableDevices.Single(d => d.Id == secondaryId);
        Assert.True(editVm.AddTargetCommand.CanExecute(null));
        editVm.AddTargetCommand.Execute(null);
        Assert.Equal(2, editVm.Targets.Count);
        Assert.Equal(secondaryId, editVm.Targets.Last().DeviceId);

        await editVm.SaveCommand.ExecuteAsync(null);

        // the regression: no false conflict, real save, draft intact, version advanced by one
        Assert.False(editVm.ConflictDetected);
        Assert.Null(editVm.ConflictMessage);
        Assert.Null(editVm.ErrorMessage);
        Assert.NotNull(editVm.SaveOutcomeNotice);
        Assert.Contains("サーバー保存成功", editVm.SaveOutcomeNotice);
        Assert.Equal(2, editVm.Targets.Count);
        Assert.Equal(2L, editVm.IfMatchVersion);
        Assert.Equal(primaryId, editVm.Targets[0].DeviceId);
        Assert.Contains(editVm.Targets, t => t.DeviceId == secondaryId);

        // server truth
        var groups = await client.ListAlarmGroupsAsync();
        Assert.True(groups.IsSuccess);
        var serverGroup = groups.Value!.Single(g => g.Id == groupId);
        Assert.Equal(2L, serverGroup.Version);
        Assert.Equal(2, serverGroup.Targets.Count);
        Assert.Contains(serverGroup.Targets, t => t.DeviceId == secondaryId);

        // --- a follow-up save (draft edits only) stays a normal 200 update; no data was lost ---
        Assert.True(editVm.SaveCommand.CanExecute(null));
        editVm.Label = "T176 メンバー追加（更新）";
        await editVm.SaveCommand.ExecuteAsync(null);
        Assert.False(editVm.ConflictDetected);
        Assert.Null(editVm.ErrorMessage);
        Assert.Equal(3L, editVm.IfMatchVersion);
        Assert.Equal(2, editVm.Targets.Count);

        // --- genuine concurrent change still conflicts, and the retry stays possible ---
        var direct = await client.ReplaceAlarmGroupAsync(
            groupId,
            new AlarmGroupWriteRequest(
                groupId,
                "T176 サーバー側更新",
                true,
                "weekly",
                new[] { 1, 2, 3 },
                null,
                "Asia/Tokyo",
                true,
                Array.Empty<string>(),
                new[]
                {
                    new AlarmTargetDto(null, primaryId, "08:00", "default", 80, true, true),
                    new AlarmTargetDto(null, secondaryId, "08:15", "default", 80, true, true),
                }),
            ifMatchVersion: editVm.IfMatchVersion);
        Assert.True(direct.IsSuccess);

        editVm.Label = "T176 ローカル下書き";
        await editVm.SaveCommand.ExecuteAsync(null);

        Assert.True(editVm.ConflictDetected);
        Assert.Contains("ETag 412", editVm.ConflictMessage);
        Assert.Equal("T176 ローカル下書き", editVm.Label);   // draft preserved
        Assert.Equal(2, editVm.Targets.Count);               // added member preserved
        Assert.True(editVm.SaveCommand.CanExecute(null));    // retry is not blocked by the banner

        // Reloading picks up the server version, so a subsequent save is accepted again.
        await editVm.ReloadFromServerCommand.ExecuteAsync(null);
        Assert.False(editVm.ConflictDetected);
        Assert.Equal(direct.Value!.Group.Version, editVm.IfMatchVersion);
        Assert.Equal("T176 サーバー側更新", editVm.Label);
    }
}
