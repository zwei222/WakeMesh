using System;
using System.Collections.Generic;
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
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// T261 — characterization of an already-implemented behaviour: non-empty <c>ExcludedDates</c> survive the
/// production group-edit save, a fresh view-model reopen, the server list, and the desired → applier path.
///
/// This case exercises two excluded dates through an existing-group edit, a fresh view model and
/// local application. <c>T16ReviewProbes.Verify_LabelOnlySavePreservesExcludedDates_AcrossApiRoundTrip</c>
/// covers the narrower VM-to-server round trip; this test also checks the desired item's excluded dates,
/// target identities and version, and the resulting local alarm. It uses the same composition as
/// <c>T176MemberAddVerticalTests</c>/<c>T22WeekdayContractRoundTripTests</c>: real
/// <see cref="CloudUiTestApiFactory"/> (in-memory SQLite test host, self-signed test token — no live endpoint,
/// no real credentials), real <see cref="WakeMeshApiClient"/>, real <see cref="CloudManagementService"/>,
/// production <see cref="CloudAlarmGroupEditViewModel"/>, production <see cref="MobileDesiredApplier"/> via
/// <see cref="CloudManagementService.SyncCurrentDeviceAsync"/>, real <see cref="AlarmCoordinator"/> over a
/// temp-file <see cref="JsonFileAlarmStore"/>.
///
/// Contract notes:
/// - the server stores/returns the excluded-date list as sent (no sort is promised), so this test pins
///   <em>set</em> equality for the VM/server hops and the applier's documented sorted order for the local alarm;
/// - null/empty payloads are out of scope here (T22/T176 already carry the empty case); this test only ever
///   asserts the two non-empty dates, so an "empty became non-empty" regression cannot pass by accident.
/// </summary>
public sealed class T261ExcludedDatesSaveRoundTripTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private static readonly string[] ExpectedDates = { "2026-12-25", "2027-01-01" };

    private readonly CloudUiTestApiFactory _factory;
    private readonly string _root;

    public T261ExcludedDatesSaveRoundTripTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _root = Path.Combine(Path.GetTempPath(), "wakemesh-t261-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NonEmptyExcludedDates_PreservedAcrossExistingGroupEditSave_FreshReopen_AndDesiredApply()
    {
        var (client, service, coordinator, deviceId) = await CreateContextAsync("excluded-dates");

        // Second member so "existing target ids stay put" is asserted on a populated group, not a singleton.
        var secondary = await client.RegisterDeviceAsync("T261-Secondary", persistCredential: false);
        Assert.True(secondary.IsSuccess, secondary.Message);
        var secondaryDeviceId = secondary.Value!.Id;

        // --- create the group through the real view model, with two distinct excluded dates ---
        var createVm = new CloudAlarmGroupEditViewModel(service);
        await createVm.InitializeAsync();
        Assert.True(createVm.IsSaveReady);

        ClearDays(createVm);
        createVm.DayMonday = true;
        createVm.Label = "T261 除外日";
        createVm.ExcludeHolidays = false;
        createVm.Targets[0].TimeOfDay = new TimeSpan(7, 30, 0);
        createVm.SelectedDeviceToAdd = createVm.AvailableDevices.Single(d => d.Id == secondaryDeviceId);
        createVm.AddTargetCommand.Execute(null);
        Assert.Equal(2, createVm.Targets.Count);

        foreach (var date in ExpectedDates)
        {
            createVm.AddExcludedDate(date);
        }

        Assert.Equal(2, createVm.ExcludedDates.Count);

        await createVm.SaveCommand.ExecuteAsync(null);

        Assert.Null(createVm.ErrorMessage);
        Assert.False(createVm.ConflictDetected);
        Assert.Equal(1L, createVm.IfMatchVersion);
        var groupId = createVm.GroupId;
        Assert.False(string.IsNullOrWhiteSpace(groupId));

        var created = await ReadGroupAsync(client, groupId);
        Assert.Equal(1L, created.Version);
        AssertSameDateSet(ExpectedDates, created.ExcludedDates);
        Assert.Equal(2, created.Targets.Count);
        var targetIdByDevice = created.Targets.ToDictionary(t => t.DeviceId, t => t.Id);
        Assert.Equal(2, targetIdByDevice.Count);
        Assert.NotNull(targetIdByDevice[deviceId]);
        Assert.NotNull(targetIdByDevice[secondaryDeviceId]);

        // --- edit intent on the existing id, one scalar change, real SaveCommand ---
        var editVm = new CloudAlarmGroupEditViewModel(service);
        await editVm.InitializeAsync(groupId);

        Assert.Null(editVm.ErrorMessage);
        Assert.False(editVm.IsNew);
        Assert.Equal(groupId, editVm.GroupId);
        Assert.Equal(1L, editVm.IfMatchVersion);
        AssertSameDateSet(ExpectedDates, editVm.ExcludedDates);
        Assert.Equal(2, editVm.ExcludedDates.Count);
        Assert.Equal(2, editVm.Targets.Count);
        Assert.Equal(targetIdByDevice[deviceId], editVm.Targets.Single(t => t.DeviceId == deviceId).Id);
        Assert.Equal(targetIdByDevice[secondaryDeviceId], editVm.Targets.Single(t => t.DeviceId == secondaryDeviceId).Id);

        editVm.Label = "T261 除外日（保存後）";
        await editVm.SaveCommand.ExecuteAsync(null);

        Assert.False(editVm.ConflictDetected);
        Assert.Null(editVm.ConflictMessage);
        Assert.Null(editVm.ErrorMessage);
        Assert.Equal(2L, editVm.IfMatchVersion);

        // --- real server GET / re-list: id, version, label, dates, target ids and devices all preserved ---
        var afterSave = await ReadGroupAsync(client, groupId);
        Assert.Equal(groupId, afterSave.Id);
        Assert.Equal(2L, afterSave.Version);
        Assert.Equal("T261 除外日（保存後）", afterSave.Label);
        Assert.Equal(new[] { 1 }, afterSave.Days!.OrderBy(d => d).ToArray());
        AssertSameDateSet(ExpectedDates, afterSave.ExcludedDates);
        Assert.Equal(2, afterSave.Targets.Count);
        Assert.Equal(targetIdByDevice[deviceId], afterSave.Targets.Single(t => t.DeviceId == deviceId).Id);
        Assert.Equal(targetIdByDevice[secondaryDeviceId], afterSave.Targets.Single(t => t.DeviceId == secondaryDeviceId).Id);

        // --- fresh view model reopen (new instance, same service) ---
        var reopened = new CloudAlarmGroupEditViewModel(service);
        await reopened.InitializeAsync(groupId);

        Assert.Null(reopened.ErrorMessage);
        Assert.False(reopened.IsNew);
        Assert.True(reopened.IsSaveReady);
        Assert.Equal(groupId, reopened.GroupId);
        Assert.Equal(2L, reopened.IfMatchVersion);
        Assert.Equal("T261 除外日（保存後）", reopened.Label);
        AssertSameDateSet(ExpectedDates, reopened.ExcludedDates);
        Assert.Equal(2, reopened.ExcludedDates.Count);
        Assert.True(reopened.DayMonday);
        Assert.Equal(2, reopened.Targets.Count);
        Assert.Equal(targetIdByDevice[deviceId], reopened.Targets.Single(t => t.DeviceId == deviceId).Id);
        Assert.Equal(targetIdByDevice[secondaryDeviceId], reopened.Targets.Single(t => t.DeviceId == secondaryDeviceId).Id);

        // --- desired → real applier → local alarm: the same two dates arrive on the device ---
        var desired = await client.GetDesiredAsync(deviceId);
        Assert.True(desired.IsSuccess, desired.Message);
        var item = Assert.Single(desired.Value!.Items, i => i.GroupId == groupId);
        Assert.Equal(targetIdByDevice[deviceId], item.ScheduleId);
        Assert.NotNull(item.ExcludedDates);
        AssertSameDateSet(ExpectedDates, item.ExcludedDates!);

        var sync = await service.SyncCurrentDeviceAsync();
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, sync.Status);

        var alarm = Assert.Single(await coordinator.GetAllAsync());
        Assert.Equal("T261 除外日（保存後）", alarm.Name);
        Assert.Equal(
            ExpectedDates.Select(DateOnly.Parse).OrderBy(d => d).ToArray(),
            alarm.ExcludedDates.OrderBy(d => d).ToArray());
        Assert.Equal(2, alarm.ExcludedDates.Count);
        Assert.Equal(new TimeOnly(7, 30), alarm.Time);
    }

    private static void AssertSameDateSet(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static void ClearDays(CloudAlarmGroupEditViewModel vm)
    {
        vm.DayMonday = false;
        vm.DayTuesday = false;
        vm.DayWednesday = false;
        vm.DayThursday = false;
        vm.DayFriday = false;
        vm.DaySaturday = false;
        vm.DaySunday = false;
    }

    private static async Task<AlarmGroupDto> ReadGroupAsync(WakeMeshApiClient client, string groupId)
    {
        var listed = await client.ListAlarmGroupsAsync();
        Assert.True(listed.IsSuccess, listed.Message);
        return Assert.Single(listed.Value!, g => g.Id == groupId);
    }

    private async Task<(WakeMeshApiClient Client, CloudManagementService Service, AlarmCoordinator Coordinator, Guid DeviceId)>
        CreateContextAsync(string leaf)
    {
        var dir = Path.Combine(_root, leaf);
        Directory.CreateDirectory(dir);

        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var auth = new MockAuthService("https://api.wakemesh.local", token);
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var reg = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

        WakeMeshApiClient ClientFactory() => TestClientHelper.CreateApiClient(http, auth, creds);
        var service = new CloudManagementService(auth, reg, creds, coordinator, ClientFactory, customStorageDir: dir);

        var registration = await service.RegisterCurrentDeviceAsync("t261-" + leaf);
        Assert.True(registration.IsSuccess, registration.Message);

        return (ClientFactory(), service, coordinator, registration.Value!.Id);
    }
}
