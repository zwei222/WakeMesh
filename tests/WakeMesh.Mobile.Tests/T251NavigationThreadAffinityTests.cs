// T251: overview(アラーム一覧) -> group editor navigation must cross the Shell boundary on the UI thread.
//
// Path B (alarm list) reaches ICloudNavigation after two ConfigureAwait(false) hops - a genuinely
// asynchronous cloud group read and the status-message TCS - so the real MauiShellCloudNavigation, not
// the caller, has to marshal Shell.GoToAsync onto the main thread. Path A (cloud tab) already enters the
// same boundary on the UI thread, which is why the same group opens from there.
//
// These probes drive the REAL AlarmListViewModel.OpenGroupCommand into the REAL
// MauiShellCloudNavigation, substituting only the unavoidable native Shell backend (internal test seam),
// with a single-threaded UI pump standing in for the Android main thread. They are thread-affinity
// composition probes: they do not prove a physical native Shell push or any renderer output.
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
using Xunit;

namespace WakeMesh.Mobile.Tests;

public sealed class T251NavigationThreadAffinityTests
{
    private static readonly Guid DeviceId = Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd");
    private static readonly Guid ScheduleS = Guid.Parse("33333333-cccc-4ccc-8ccc-333333333333");
    private const string OwnerGroupId = "c0ffee00-1111-4111-8111-000000000001";
    private static readonly string EditRoute =
        "CloudAlarmGroupEditPage?groupId=" + Uri.EscapeDataString(OwnerGroupId);

    // --- 1) the reported defect: the boundary must run on the UI thread -------------------------

    [Fact]
    public void OverviewOpen_InvokesTheNativeBoundaryOnTheUiThread_ExactlyOnce()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var boundary = new T251Boundary();
        var navigation = new MauiShellCloudNavigation(dispatcher, boundary.Invoke);
        var cloud = new T251Cloud();
        var vm = NewViewModel(cloud, navigation, dispatcher);

        pump.Run(async () =>
        {
            var row = Row(ScheduleS, "stale-manifest-id");
            Assert.True(vm.OpenGroupCommand.CanExecute(row));
            await vm.OpenGroupCommand.ExecuteAsync(row);
        });

        Assert.Equal(1, boundary.Count);
        Assert.Equal(pump.ThreadId, boundary.ThreadId);            // RED before the marshal: a pool thread
        Assert.Equal(EditRoute, boundary.Route);                   // same route / resolved owner group
        Assert.Equal(1, cloud.ListGroupsCallCount);
        Assert.NotEqual(0, boundary.ThreadId);
        // The cloud double really completed off the UI thread, so a synchronous double cannot mask the
        // ConfigureAwait(false) continuation this probe depends on.
        Assert.NotEqual(pump.ThreadId, cloud.CompletionThreadId);
    }

    // --- 2) UI entry (the working cloud-tab shape) stays inline and awaits the transition -------

    [Fact]
    public void UiThreadEntry_RunsTheBoundaryInline_AndAwaitsTheFullNativeTransition()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var native = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new T251Boundary(native.Task);
        var navigation = new MauiShellCloudNavigation(dispatcher, boundary.Invoke);

        Task? pending = null;
        pump.Run(async () =>
        {
            pending = navigation.GoToAsync(EditRoute);

            Assert.Equal(1, boundary.Count);                        // no extra dispatch hop on the UI thread
            Assert.Equal(pump.ThreadId, boundary.ThreadId);
            Assert.False(pending.IsCompleted);                      // not completed on enqueue

            native.SetResult(null);                                 // end of the native transition
            await pending;
        });

        Assert.NotNull(pending);
        Assert.True(pending!.IsCompletedSuccessfully);
        Assert.Equal(1, boundary.Count);
    }

    // --- 3) worker entry (the alarm-list shape) marshals and awaits the transition --------------

    [Fact]
    public void WorkerEntry_MarshalsToTheUiThread_AndCompletesOnlyAfterTheNativeTransition()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var native = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new T251Boundary(native.Task);
        var navigation = new MauiShellCloudNavigation(dispatcher, boundary.Invoke);

        Task? pending = null;
        pump.Run(async () =>
        {
            pending = Task.Run(() => navigation.GoToAsync(EditRoute));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (boundary.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(1, boundary.Count);
            Assert.Equal(pump.ThreadId, boundary.ThreadId);         // the post landed on the UI thread
            Assert.False(pending!.IsCompleted);                     // still running the native transition

            native.SetResult(null);
            await pending!;
        });

        Assert.Equal(1, boundary.Count);
        Assert.True(pending!.IsCompletedSuccessfully);
    }

    // --- 4) failures cross the boundary: sync throw, async fault, dispatcher Post failure ------

    [Fact]
    public void SynchronousNativeThrow_IsPropagated()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var navigation = new MauiShellCloudNavigation(
            dispatcher,
            _ => throw new InvalidOperationException("shell exploded"));

        pump.Run(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => navigation.GoToAsync(EditRoute));
            Assert.Equal("shell exploded", ex.Message);
        });
    }

    [Fact]
    public void AsynchronousNativeFault_IsPropagated()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var navigation = new MauiShellCloudNavigation(
            dispatcher,
            _ => Task.FromException(new TimeoutException("native transition timed out")));

        pump.Run(async () =>
        {
            // Called from a worker thread, so the exception has to travel back through the dispatch.
            var call = Task.Run(() => navigation.GoToAsync(EditRoute));
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => call);
            Assert.Equal("native transition timed out", ex.Message);
        });
    }

    [Fact]
    public async Task PostFailure_IsPropagated_AndTheNativeBoundaryIsNotInvoked()
    {
        var boundary = new T251Boundary();
        var navigation = new MauiShellCloudNavigation(new T251ThrowingDispatcher(), boundary.Invoke);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => navigation.GoToAsync(EditRoute));

        Assert.Equal("no ui queue", ex.Message);
        Assert.Equal(0, boundary.Count);
    }

    // --- 5) preserved contracts: route validation, gate, resolver ------------------------------

    [Fact]
    public async Task BlankRoute_IsRejectedBeforeAnyDispatch()
    {
        var boundary = new T251Boundary();
        var navigation = new MauiShellCloudNavigation(new T251ThrowingDispatcher(), boundary.Invoke);

        await Assert.ThrowsAsync<ArgumentException>(() => navigation.GoToAsync("  "));
        await Assert.ThrowsAsync<ArgumentException>(() => navigation.GoToAsync(string.Empty));
        Assert.Equal(0, boundary.Count);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MauiShellCloudNavigation((IMainThreadDispatcher)null!));
    }

    [Fact]
    public void UnopenableOrUnresolvedRows_NeverReachTheNativeBoundary()
    {
        using var pump = new T123UiPump();
        var dispatcher = new T251UiDispatcher(pump);
        var boundary = new T251Boundary();
        var navigation = new MauiShellCloudNavigation(dispatcher, boundary.Invoke);
        var cloud = new T251Cloud();
        var vm = NewViewModel(cloud, navigation, dispatcher);

        pump.Run(async () =>
        {
            await vm.OpenGroupCommand.ExecuteAsync(Row(null, "stale-manifest-id"));      // no schedule id
            await vm.OpenGroupCommand.ExecuteAsync(Row(ScheduleS, string.Empty));        // row gate: no group id
            await vm.OpenGroupCommand.ExecuteAsync(Row(Guid.NewGuid(), "stale-manifest-id")); // unresolved owner
        });

        Assert.Equal(0, boundary.Count);
        Assert.Equal(1, cloud.ListGroupsCallCount);   // only the resolvable row read the server
    }

    // --- helpers ------------------------------------------------------------------------------

    private static AlarmListViewModel NewViewModel(
        T251Cloud cloud, ICloudNavigation navigation, IMainThreadDispatcher dispatcher)
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "wakemesh_t251_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(System.IO.Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new JapaneseHolidayService(holidays: Array.Empty<DateOnly>())),
            new MockAlarmScheduler());

        return new AlarmListViewModel(
            coordinator,
            new JapaneseHolidayService(holidays: Array.Empty<DateOnly>()),
            new LocalDeviceInfoProvider("t251-device"),
            dispatcher: dispatcher,
            cloud: cloud,
            navigation: navigation);
    }

    private static GroupAlarmRow Row(Guid? scheduleId, string groupId, string groupName = "グループ朝") => new(
        RowKey: "row-" + (scheduleId?.ToString("N") ?? "none") + "-" + groupId,
        Origin: GroupAlarmOrigin.CloudApplied,
        LocalAlarmId: "manual-t251",
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

    /// <summary>The native boundary replacement: records who ran it and how often.</summary>
    private sealed class T251Boundary
    {
        private int _count;
        private int _threadId;
        private string? _route;
        private readonly Task _nativeResult;

        public T251Boundary(Task? nativeResult = null) => _nativeResult = nativeResult ?? Task.CompletedTask;

        public int Count => Volatile.Read(ref _count);

        public int ThreadId => Volatile.Read(ref _threadId);

        public string? Route => _route;

        public Task Invoke(string route)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            _route = route;
            return _nativeResult;
        }
    }

    /// <summary>Same shape as the Android dispatcher: it marshals only off the UI thread.</summary>
    private sealed class T251UiDispatcher : IMainThreadDispatcher
    {
        private readonly T123UiPump _pump;

        public T251UiDispatcher(T123UiPump pump) => _pump = pump ?? throw new ArgumentNullException(nameof(pump));

        public bool IsDispatchRequired => Environment.CurrentManagedThreadId != _pump.ThreadId;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            _pump.Post(_ => action(), null);
        }
    }

    private sealed class T251ThrowingDispatcher : IMainThreadDispatcher
    {
        public bool IsDispatchRequired => true;

        public void Post(Action action) => throw new InvalidOperationException("no ui queue");
    }

    /// <summary>Real-contract cloud double whose group read completes on a worker thread.</summary>
    private sealed class T251Cloud : ICloudManagementService
    {
        private int _listGroupsCallCount;

        public bool IsConfiguredAndSignedIn { get; set; } = true;
        public string? CurrentUserAccountId { get; set; } = "owner-account";

        public int ListGroupsCallCount => Volatile.Read(ref _listGroupsCallCount);
        public int CompletionThreadId { get; private set; }

        public event EventHandler? RegistrationChanged { add { } remove { } }
        public event EventHandler? SyncCompleted { add { } remove { } }

        public async Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _listGroupsCallCount);

            // Genuinely asynchronous, like HTTP: the continuation must not return to the caller's thread.
            await Task.Delay(20).ConfigureAwait(false);

            CompletionThreadId = Environment.CurrentManagedThreadId;

            return ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(new[] { Group(OwnerGroupId, ScheduleS) });
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
    }
}
