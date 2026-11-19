// T191 (T187R follow-up): open-from-list must hand the editor the *server's* owning group id.
//
// The alarm-list row carries a GroupId copied from the device's sync manifest. If that copy is stale
// the editor was opened with an id that no longer exists, which (before the T186R guard) fell through
// to the new-group defaults and could create a second group. The row's ScheduleId is a server target
// id (DeviceSchedule.Id), the same value AlarmGroupDto.Targets[].Id carries, and each schedule has a
// single owning group - so a live server read resolves the current owner exactly.
// These probes drive the real OpenGroupCommand with a fake ICloudManagementService + fake ICloudNavigation.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.Tests;

public sealed class T191GroupOpenResolverTests
{
    private static readonly Guid DeviceId = Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd");
    private static readonly Guid ScheduleS = Guid.Parse("33333333-cccc-4ccc-8ccc-333333333333");
    private static readonly Guid OtherSchedule = Guid.Parse("44444444-dddd-4ddd-8ddd-444444444444");

    private const string FixedFailure = "グループを特定できませんでした。一覧を更新してください";

    // --- 1) stale manifest GroupId is replaced by the authoritative server id -------------------

    [Fact]
    public async Task StaleRowGroupId_OpensTheServerOwnerGroup_ExactlyOnce()
    {
        var cloud = new ResolverCloud();
        cloud.Result = Ok(
            Group("g-old", OtherSchedule),          // the row's stale id now owns a different schedule
            Group("g-new", ScheduleS));             // the current owner of this row's schedule
        var routes = new List<string>();
        var vm = NewViewModel(cloud, routes);

        var row = Row(ScheduleS, "g-old");

        Assert.True(vm.OpenGroupCommand.CanExecute(row));
        await vm.OpenGroupCommand.ExecuteAsync(row);

        var route = Assert.Single(routes);
        Assert.Equal($"CloudAlarmGroupEditPage?groupId={Uri.EscapeDataString("g-new")}", route);
        Assert.Equal(1, cloud.ListGroupsCallCount);
        Assert.DoesNotContain("g-old", route);
    }

    // --- 2) no unique owner: no navigation, no label/id fallback --------------------------------

    [Fact]
    public async Task UnresolvedOwner_NeverFallsBackToTheRowIdOrTheLabel()
    {
        // (a) no group carries this schedule id
        var cloud = new ResolverCloud();
        cloud.Result = Ok(Group("g-other", OtherSchedule));
        var routes = new List<string>();
        var vm = NewViewModel(cloud, routes);

        var row = Row(ScheduleS, "g-stale");
        await vm.OpenGroupCommand.ExecuteAsync(row);

        Assert.Empty(routes);
        Assert.Equal(FixedFailure, vm.StatusMessage);

        // (b) a group with the same label but a different target is not opened
        cloud.Result = Ok(Group("g-same-label", OtherSchedule));
        var sameLabel = Row(ScheduleS, "g-stale", groupName: "グループ朝");
        await vm.OpenGroupCommand.ExecuteAsync(sameLabel);
        Assert.Empty(routes);
        Assert.Equal(FixedFailure, vm.StatusMessage);

        // (c) two groups claim the same schedule id: ambiguous, refuse instead of guessing
        cloud.Result = Ok(Group("g-a", ScheduleS), Group("g-b", ScheduleS));
        await vm.OpenGroupCommand.ExecuteAsync(row);
        Assert.Empty(routes);
        Assert.Equal(FixedFailure, vm.StatusMessage);

        Assert.Equal(3, cloud.ListGroupsCallCount);
    }

    // --- 3) API failure / exception: no navigation, fixed message ------------------------------

    [Fact]
    public async Task ApiFailureOrThrow_DoesNotNavigateAndReportsAFixedMessage()
    {
        var cloud = new ResolverCloud
        {
            Result = ApiResult<IReadOnlyList<AlarmGroupDto>>.Fail(
                ApiErrorKind.Unauthorized, statusCode: 401, message: "unauthorized")
        };
        var routes = new List<string>();
        var vm = NewViewModel(cloud, routes);
        var row = Row(ScheduleS, "g-stale");

        await vm.OpenGroupCommand.ExecuteAsync(row);
        Assert.Empty(routes);
        Assert.Equal(FixedFailure, vm.StatusMessage);

        cloud.ListGroupsException = new InvalidOperationException("groups endpoint down");
        await vm.OpenGroupCommand.ExecuteAsync(row);
        Assert.Empty(routes);
        Assert.DoesNotContain("groups endpoint down", vm.StatusMessage ?? string.Empty);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));

        // never a route that would open the new-group editor
        Assert.DoesNotContain(routes, r => r.Contains("new", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, cloud.ListGroupsCallCount);
    }

    // --- 4) no usable schedule id: no API call, no navigation ----------------------------------

    [Fact]
    public async Task MissingOrEmptyScheduleId_DoesNotCallTheApiAndDoesNotNavigate()
    {
        var cloud = new ResolverCloud();
        cloud.Result = Ok(Group("g-new", ScheduleS));
        var routes = new List<string>();
        var vm = NewViewModel(cloud, routes);

        await vm.OpenGroupCommand.ExecuteAsync(Row(null, "g-stale"));
        await vm.OpenGroupCommand.ExecuteAsync(Row(Guid.Empty, "g-stale"));

        Assert.Empty(routes);
        Assert.Equal(0, cloud.ListGroupsCallCount);      // not even a read
        Assert.Equal(FixedFailure, vm.StatusMessage);
    }

    // --- 5) progress text in the existing footer, then restored --------------------------------

    [Fact]
    public async Task WhileResolving_TheExistingFooterShowsProgress_ThenReturnsToTheNormalText()
    {
        var cloud = new ResolverCloud { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        cloud.Result = Ok(Group("g-new", ScheduleS));
        var routes = new List<string>();
        var vm = NewViewModel(cloud, routes);

        var run = vm.OpenGroupCommand.ExecuteAsync(Row(ScheduleS, "g-old"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (cloud.ListGroupsCallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, cloud.ListGroupsCallCount);
        Assert.Equal("グループを開いています...", vm.StatusMessage);
        Assert.Empty(routes);

        // A second tap while the resolve is in flight must not start a second read.
        await vm.OpenGroupCommand.ExecuteAsync(Row(ScheduleS, "g-old"));
        Assert.Equal(1, cloud.ListGroupsCallCount);

        cloud.Gate!.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var route = Assert.Single(routes);
        Assert.Equal($"CloudAlarmGroupEditPage?groupId={Uri.EscapeDataString("g-new")}", route);
        Assert.NotEqual("グループを開いています...", vm.StatusMessage);
        Assert.Equal("0 件のアラーム (0 件有効)", vm.StatusMessage);   // footer wording restored, not left blank
    }

    // --- helpers ------------------------------------------------------------------------------

    private static AlarmListViewModel NewViewModel(ResolverCloud cloud, List<string> routes)
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "wakemesh_t191_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(System.IO.Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new JapaneseHolidayService(holidays: Array.Empty<DateOnly>())),
            new MockAlarmScheduler());

        return new AlarmListViewModel(
            coordinator,
            new JapaneseHolidayService(holidays: Array.Empty<DateOnly>()),
            new LocalDeviceInfoProvider("t191-device"),
            cloud: cloud,
            navigation: new ActionCloudNavigation(route => routes.Add(route)));
    }

    private static GroupAlarmRow Row(Guid? scheduleId, string? groupId, string groupName = "グループ朝") => new(
        RowKey: "row-" + (scheduleId?.ToString("N") ?? "none") + "-" + (groupId ?? "null"),
        Origin: GroupAlarmOrigin.CloudApplied,
        LocalAlarmId: "manual-t191",
        ScheduleId: scheduleId,
        GroupId: groupId,
        GroupName: groupName,
        TimeText: "07:00",
        DaysText: "平日 (月〜金)",
        NextFireText: null,
        IsEnabled: true,
        EnabledText: "オン",
        TimeZoneNote: null,
        SkipHolidays: true,
        HasHolidayCoverageWarning: false,
        IsRegistrationFailed: false,
        PendingText: null,
        WarningText: null,
        CanOpenGroup: true);

    private static AlarmGroupDto Group(string id, params Guid?[] targetIds)
    {
        var targets = new List<AlarmTargetDto>();
        foreach (var targetId in targetIds)
        {
            targets.Add(new AlarmTargetDto(targetId, DeviceId, "07:00", "default", 80, true, true));
        }

        return new AlarmGroupDto(
            id, "グループ朝", true, 1, null, "weekly", new[] { 1, 2, 3, 4, 5 }, null,
            "Asia/Tokyo", true, Array.Empty<string>(), targets);
    }

    private static ApiResult<IReadOnlyList<AlarmGroupDto>> Ok(params AlarmGroupDto[] groups) =>
        ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(groups);

    /// <summary>Minimal real-contract cloud double: only the read-only list is meaningful here.</summary>
    private sealed class ResolverCloud : ICloudManagementService
    {
        public bool IsConfiguredAndSignedIn { get; set; } = true;
        public string? CurrentUserAccountId { get; set; } = "owner-account";

        public int ListGroupsCallCount { get; private set; }
        public ApiResult<IReadOnlyList<AlarmGroupDto>> Result { get; set; } =
            ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(Array.Empty<AlarmGroupDto>());
        public Exception? ListGroupsException { get; set; }
        public TaskCompletionSource? Gate { get; set; }

        public event EventHandler? RegistrationChanged { add { } remove { } }
        public event EventHandler? SyncCompleted { add { } remove { } }

        public async Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(
            CancellationToken cancellationToken = default)
        {
            ListGroupsCallCount++;

            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            if (ListGroupsException is not null)
            {
                throw ListGroupsException;
            }

            return Result;
        }

        public Task<CloudDeviceRegistration?> GetCurrentRegistrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudDeviceRegistration?>(null);

        public Task<ApiResult<DeviceRegisterResponse>> RegisterCurrentDeviceAsync(
            string displayName, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task ClearCurrentRegistrationRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<DeviceListItem>>.Ok(Array.Empty<DeviceListItem>()));

        public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
            AlarmGroupWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
            string groupId, AlarmGroupWriteRequest request, long ifMatchVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<GroupSaveOutcome>.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<ApiResult> DeleteAlarmGroupAsync(
            string groupId, long ifMatchVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Fail(ApiErrorKind.Network, message: "stub"));

        public Task<DesiredSyncOutcome> SyncCurrentDeviceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, null, "stub"));
    }
}
