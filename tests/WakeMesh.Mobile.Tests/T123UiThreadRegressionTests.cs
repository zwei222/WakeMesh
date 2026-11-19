using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T123: the alarm list is a page view model, so every observable change it makes (collection Replace /
/// Remove, <see cref="AlarmListViewModel.IsBusy"/>, StatusMessage, readiness, the ringing banner and the
/// commands' CanExecuteChanged) has to happen on the UI thread. These tests give the view model a real
/// single-threaded UI pump plus a dispatcher that always marshals, and a <see cref="GroupAlarmSource"/>
/// whose registration read finishes on a worker thread — the exact production shape that used to make
/// <c>ConfigureAwait(false)</c> drop the UI thread.
///
/// They also pin the two confirmed minor defects: a manual load must wait for the projection gate instead
/// of skipping its projection, and a toggle / delete that deferred a sync notification must run exactly one
/// catch-up projection (read-only: never an OS reschedule) when it finishes.
/// </summary>
public sealed class T123UiThreadRegressionTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd");
    private static readonly Guid ScheduleA = Guid.Parse("33333333-cccc-4ccc-8ccc-333333333333");
    private const string ManualId = "manual-t123";

    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly T123RecordingAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly CloudSyncBinding _binding;
    private readonly FileCloudSyncManifestStore _manifestStore;
    private readonly MobileDesiredApplier _applier;
    private readonly T123WorkerRegistrationStore _registrationStore;
    private readonly GroupAlarmSource _groupSource;
    private readonly DesiredObservationStore _observations;
    private readonly T123UiPump _pump;
    private readonly T123UiDispatcher _dispatcher;
    private readonly T123PermissionService _permissions;

    public T123UiThreadRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t123_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new T123RecordingAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);

        _binding = new CloudSyncBinding("https://api.wakemesh.local", "owner-account", DeviceId, 1);
        _manifestStore = new FileCloudSyncManifestStore(
            _binding, CloudSyncStoragePaths.Manifest(_tempDir, _binding));
        _applier = new MobileDesiredApplier(_binding, _coordinator, _manifestStore);

        _registrationStore = new T123WorkerRegistrationStore(new CloudDeviceRegistration(
            DeviceId, 1, _binding.ApiBaseUrl, _binding.AccountId, "Fold5"));
        _groupSource = new GroupAlarmSource(_registrationStore, _tempDir);
        _observations = new DesiredObservationStore();
        _permissions = new T123PermissionService();

        _pump = new T123UiPump();
        _dispatcher = new T123UiDispatcher(_pump);
    }

    public void Dispose()
    {
        _pump.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // --- 1) manual ON/OFF (T105/T109 path) stays on the UI thread -----------

    [Fact]
    public void ManualSwitch_KeepsItsReplaceAndEveryNotificationOnTheUiThread()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var vm = NewViewModel();
            await vm.LoadAlarmsAsync();

            var alarm = Assert.Single(vm.Alarms);
            Assert.True(alarm.IsEnabled);

            using var recorder = new T123NotificationRecorder(vm);
            var registrationReadsBefore = _registrationStore.LoadCount;
            var switched = await vm.SetAlarmEnabledFromUserAsync(alarm, false);

            Assert.True(switched);
            Assert.False(vm.IsBusy);
            recorder.AssertAllOnUiThread(_pump.ThreadId);

            var replaced = Assert.Single(vm.Alarms);
            Assert.False(replaced.IsEnabled);
            Assert.Contains(recorder.Events, e => e.Source == "Alarms" && e.Action == NotifyCollectionChangedAction.Replace);
            Assert.Contains(recorder.Events, e => e.Source == "vm" && e.Property == nameof(AlarmListViewModel.IsBusy));
            Assert.Contains(recorder.Events, e => e.Source == "cmd:Toggle" );

            var stored = await _store.GetByIdAsync(ManualId);
            Assert.NotNull(stored);
            Assert.False(stored!.IsEnabled);

            // The cloud-managed guard really read the registration on a worker thread before returning.
            Assert.True(_registrationStore.LoadCount > registrationReadsBefore);
        });
    }

    // --- 2) manual delete stays on the UI thread ---------------------------

    [Fact]
    public void ManualDelete_KeepsItsRemoveAndEveryNotificationOnTheUiThread()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var vm = NewViewModel();
            await vm.LoadAlarmsAsync();
            Assert.Single(vm.Alarms);

            using var recorder = new T123NotificationRecorder(vm);
            var deleted = await vm.DeleteAlarmAsync(ManualId);

            Assert.True(deleted);
            Assert.False(vm.IsBusy);
            recorder.AssertAllOnUiThread(_pump.ThreadId);

            Assert.Empty(vm.Alarms);
            Assert.Contains(recorder.Events, e => e.Source == "Alarms" && e.Action == NotifyCollectionChangedAction.Remove);
            Assert.Null(await _store.GetByIdAsync(ManualId));
        });
    }

    // --- 3) the load's second half (spinner, readiness, ringing) ------------

    [Fact]
    public void ManualLoad_ReleasesSpinner_ReadinessAndRingingBannerOnTheUiThread()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var ringing = new StubAlarmRingingService();
            var vm = NewViewModel(ringing: ringing);
            await vm.LoadAlarmsAsync();

            ringing.AddForTesting(new ActiveRingingAlarm(
                ManualId, "手動アラーム", DateTimeOffset.UtcNow, 5));

            using var recorder = new T123NotificationRecorder(vm);
            _permissions.CanScheduleExactAlarms = false;   // makes the post-load readiness really change
            await vm.LoadAlarmsAsync();

            Assert.False(vm.IsBusy);
            Assert.Equal("Critical", vm.ReadinessLevel);
            recorder.AssertAllOnUiThread(_pump.ThreadId);

            Assert.Contains(recorder.Events, e => e.Source == "vm" && e.Property == nameof(AlarmListViewModel.IsBusy));
            Assert.Contains(recorder.Events, e => e.Source == "vm" && e.Property == nameof(AlarmListViewModel.ReadinessLevel));
            Assert.Contains(recorder.Events, e => e.Source == "RingingAlarms" && e.Action == NotifyCollectionChangedAction.Add);
            Assert.Contains(recorder.Events, e => e.Source == "vm" && e.Property == nameof(AlarmListViewModel.IsRinging));
            Assert.True(vm.IsRinging);
        });
    }

    // --- 4) two taps while the cloud-managed lookup is pending -------------

    [Fact]
    public void TwoRapidTaps_WhileTheCloudGuardIsPending_MakeExactlyOneMutation()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var gated = new T123GatedGroupAlarmSource(_groupSource);
            var vm = NewViewModel(source: gated);
            await vm.LoadAlarmsAsync();

            gated.Arm();
            var first = vm.ToggleAlarmAsync(ManualId);
            await gated.Entered;   // the guard's read-only load is now in flight

            Assert.True(vm.IsBusy);   // the busy claim is taken before the first await
            var second = vm.ToggleAlarmAsync(ManualId);
            Assert.False(await second);
            Assert.Equal(1, gated.BlockedLoadCount);

            gated.Release();
            Assert.True(await first);
            Assert.False(vm.IsBusy);

            var stored = await _store.GetByIdAsync(ManualId);
            Assert.NotNull(stored);
            Assert.False(stored!.IsEnabled);   // exactly one flip away from the saved ON
        });
    }

    // --- 5) a load must not skip its projection while the gate is held -----

    [Fact]
    public void NotifiedProjectionHoldingTheGate_DoesNotMakeTheManualLoadSkipOrProjectStale()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var counting = new T123CountingGroupAlarmSource(_groupSource);
            var gated = new T123GatedGroupAlarmSource(counting);
            var vm = NewViewModel(source: gated);
            await vm.LoadAlarmsAsync();

            var row = Assert.Single(vm.GroupAlarms);
            Assert.Equal(GroupAlarmProjector.EnabledTextOn, row.EnabledText);

            // A notification projection takes the gate and blocks inside its read-only source load.
            gated.Arm();
            RaiseSyncNotification();
            await gated.Entered;

            // The store changes after that projection already read it: its result can only be stale.
            var stored = await _store.GetByIdAsync(row.LocalAlarmId!);
            Assert.NotNull(stored);
            stored!.IsEnabled = false;
            stored.NextFireTime = null;
            await _store.SaveAsync(stored);

            // Park the manual load inside RescheduleAllActiveAsync, i.e. before its own projection attempt.
            var hold = _scheduler.Hold();
            var load = vm.LoadAlarmsAsync();
            await hold.Entered;

            hold.Release();

            // The load owns the projection: it must wait for the running one instead of skipping its pass.
            // The notification still holds the gate here, so a load that finishes now can only be one that
            // skipped its own projection.
            var finishedWithoutWaiting = await CompletesWithinAsync(load, TimeSpan.FromMilliseconds(500));
            Assert.False(
                finishedWithoutWaiting,
                "the manual load completed while the notification projection still held the gate: it skipped its own projection");

            gated.Release();
            await load;
            await vm.WaitForPendingProjectionAsync();

            // initial load + notification run + the load's own pass, in that order.
            Assert.Equal(3, counting.LoadCount);

            var refreshed = Assert.Single(vm.GroupAlarms);
            Assert.Equal(GroupAlarmProjector.EnabledTextOff, refreshed.EnabledText);
            Assert.Null(refreshed.NextFireText);
            Assert.False(vm.IsBusy);
        });
    }

    // --- 6) a notification deferred by a mutation is caught up once --------

    [Fact]
    public void NotificationDuringAToggle_RunsOneCatchUpProjection_WithoutTouchingTheOs()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var gated = new T123GatedGroupAlarmSource(_groupSource);
            var vm = NewViewModel(source: gated);
            await vm.LoadAlarmsAsync();

            var readsAfterLoad = gated.LoadCount;
            var schedulesBefore = _scheduler.ScheduledCount;
            var cancelsBefore = _scheduler.CancelledCount;

            using var recorder = new T123NotificationRecorder(vm);
            gated.Arm();
            var toggle = vm.ToggleAlarmAsync(ManualId);
            await gated.Entered;

            RaiseSyncNotification();
            await vm.WaitForPendingProjectionAsync();

            // The deferred run must not read the source: only the in-flight guard load happened so far.
            Assert.Equal(readsAfterLoad + 1, gated.LoadCount);

            gated.Release();
            Assert.True(await toggle);

            // Exactly one catch-up projection ran after the toggle (on top of the guard read), and none of it
            // rescheduled anything on the OS.
            Assert.Equal(readsAfterLoad + 2, gated.LoadCount);
            // The only OS call is the toggle's own: switching OFF cancels the registration and never arms one.
            Assert.Equal(schedulesBefore, _scheduler.ScheduledCount);
            Assert.Equal(cancelsBefore + 1, _scheduler.CancelledCount);
            Assert.False(vm.IsBusy);
            Assert.Single(vm.Alarms);
            recorder.AssertAllOnUiThread(_pump.ThreadId);
        });
    }

    // --- refused / failing operations always release IsBusy ----------------

    [Fact]
    public void NotificationDuringADelete_RunsOneCatchUpProjection_WithoutTouchingTheOs()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var gated = new T123GatedGroupAlarmSource(_groupSource);
            var vm = NewViewModel(source: gated);
            await vm.LoadAlarmsAsync();

            var readsAfterLoad = gated.LoadCount;
            var schedulesBefore = _scheduler.ScheduledCount;
            var cancelsBefore = _scheduler.CancelledCount;

            using var recorder = new T123NotificationRecorder(vm);
            gated.Arm();
            var delete = vm.DeleteAlarmAsync(ManualId);
            await gated.Entered;

            RaiseSyncNotification();
            await vm.WaitForPendingProjectionAsync();

            // Deferred while the delete owns IsBusy: still only the in-flight guard read.
            Assert.Equal(readsAfterLoad + 1, gated.LoadCount);

            gated.Release();
            Assert.True(await delete);

            Assert.Equal(readsAfterLoad + 2, gated.LoadCount);   // exactly one catch-up
            Assert.Equal(schedulesBefore, _scheduler.ScheduledCount);
            Assert.Equal(cancelsBefore + 1, _scheduler.CancelledCount);   // the delete's own cancel only
            Assert.False(vm.IsBusy);
            Assert.Empty(vm.Alarms);
            Assert.Single(vm.GroupAlarms);
            recorder.AssertAllOnUiThread(_pump.ThreadId);
        });
    }

    [Fact]
    public void RefusedAndFailingOperations_AlwaysReleaseTheBusyFlag()
    {
        _pump.Run(async () =>
        {
            await SeedAsync();
            var vm = NewViewModel();
            await vm.LoadAlarmsAsync();

            var cloudId = SyncLocalAlarmIdentity.GetLocalAlarmId(_binding, ScheduleA);
            var busyBefore = vm.IsBusy;
            Assert.False(await vm.ToggleAlarmAsync(cloudId));
            Assert.False(vm.IsBusy);
            Assert.Equal(busyBefore, vm.IsBusy);
            Assert.Single(vm.Alarms);

            var failingStore = new T123FailingMutationAlarmStore(_store);
            var throwing = new AlarmCoordinator(
                failingStore,
                new AlarmScheduleCalculator(_holidayService),
                _scheduler);
            var failingVm = NewViewModel(coordinator: throwing);
            await failingVm.LoadAlarmsAsync();
            var alarm = Assert.Single(failingVm.Alarms);

            failingStore.FailSaves = true;
            Assert.False(await failingVm.SetAlarmEnabledFromUserAsync(alarm, false));
            Assert.False(failingVm.IsBusy);
            Assert.StartsWith("切替エラー", failingVm.StatusMessage);

            failingStore.FailSaves = false;
            failingStore.FailDeletes = true;
            Assert.False(await failingVm.DeleteAlarmAsync(ManualId));
            Assert.False(failingVm.IsBusy);
            Assert.StartsWith("削除エラー", failingVm.StatusMessage);
        });
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>True when <paramref name="task"/> finished within <paramref name="timeout"/>.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        var delay = Task.Delay(timeout);
        var first = await Task.WhenAny(task, delay);
        return ReferenceEquals(first, task);
    }

    private async Task SeedAsync()
    {
        await _coordinator.SaveAndScheduleAsync(new LocalAlarmModel
        {
            Id = ManualId,
            Name = "手動アラーム",
            IsEnabled = true,
            Time = new TimeOnly(6, 30),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday
                | Weekdays.Thursday | Weekdays.Friday,
            DeviceId = "t123-device",
            TimeZoneId = "Asia/Tokyo"
        });

        var applied = await _applier.ApplyAsync(Snapshot(Item(ScheduleA)));
        Assert.True(applied.Succeeded, string.Join(",", applied.Failures.Select(f => f.Code + ":" + f.Message)));
    }

    private AlarmListViewModel NewViewModel(
        IGroupAlarmSource? source = null,
        AlarmCoordinator? coordinator = null,
        IAlarmRingingService? ringing = null) =>
        NewSubscribedViewModel(new(
            coordinator ?? _coordinator,
            _holidayService,
            new LocalDeviceInfoProvider("t123-device"),
            ringingService: ringing,
            dispatcher: _dispatcher,
            permissionService: _permissions,
            observations: _observations,
            groupSource: source ?? _groupSource));

    private static AlarmListViewModel NewSubscribedViewModel(AlarmListViewModel vm)
    {
        // The page owns this lifecycle call: it is what routes sync notifications into a re-projection.
        vm.SubscribeSyncEvents();
        return vm;
    }

    private void RaiseSyncNotification() =>
        _observations.Publish(
            _binding.ToStorageKey(),
            Snapshot(Item(ScheduleA)),
            DesiredSyncStatus.AppliedAcknowledged,
            DateTimeOffset.UtcNow);

    private static DesiredItemDto Item(Guid scheduleId, string groupId = "group-1", string label = "グループ朝") =>
        new(scheduleId, groupId, label, true, "weekly", new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo",
            true, Array.Empty<string>(), "07:00", "sound-default", 80, true, true, 10);

    private DesiredSnapshotDto Snapshot(params DesiredItemDto[] items) =>
        new(DeviceId, 1, Guid.NewGuid(), items.ToList(), new List<DesiredTombstoneDto>());
}

// --- dedicated T123 doubles (same shapes as the production contracts) -------

/// <summary>
/// Single-threaded UI pump. It installs a <see cref="SynchronizationContext"/> on the calling thread and
/// runs an async body as the "UI thread": every continuation the view model captures comes back here, and
/// anything the dispatcher posts is executed here too. No MAUI, no thread pool UI thread.
/// </summary>
internal sealed class T123UiPump : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    /// <summary>The thread id the body runs on; only valid while <see cref="Run"/> is pumping.</summary>
    public int ThreadId { get; private set; }

    public bool IsUiThread => Environment.CurrentManagedThreadId == ThreadId;

    public void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _queue.Add((callback, state));
    }

    /// <summary>Runs <paramref name="body"/> on the UI pump and drains everything it marshals back.</summary>
    public void Run(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var previous = SynchronizationContext.Current;
        ThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new T123UiSyncContext(this));

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpStart = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Post(async _ =>
            {
                try
                {
                    await body();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);

            while (!completion.Task.IsCompleted)
            {
                if (pumpStart.Elapsed > TimeSpan.FromSeconds(60))
                {
                    completion.TrySetException(new TimeoutException("the UI pump made no progress for 60s"));
                    break;
                }

                if (!_queue.TryTake(out var item, 100))
                {
                    continue;
                }

                item.Callback(item.State);
            }

            completion.Task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public void Dispose() => _queue.Dispose();

    private sealed class T123UiSyncContext : SynchronizationContext
    {
        private readonly T123UiPump _pump;

        public T123UiSyncContext(T123UiPump pump) => _pump = pump;

        public override SynchronizationContext CreateCopy() => this;

        public override void Post(SendOrPostCallback d, object? state) => _pump.Post(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_pump.IsUiThread)
            {
                d(state);
                return;
            }

            var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pump.Post(_ =>
            {
                try
                {
                    d(state);
                    done.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }, null);
            done.Task.GetAwaiter().GetResult();
        }
    }
}

/// <summary>The page's dispatcher shape: it always marshals (never "we are already on the UI thread").</summary>
internal sealed class T123UiDispatcher : IMainThreadDispatcher
{
    private readonly T123UiPump _pump;

    public T123UiDispatcher(T123UiPump pump) => _pump = pump;

    public bool IsDispatchRequired => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pump.Post(_ => action(), null);
    }
}

/// <summary>Records which thread every observable notification of the view model came from.</summary>
internal sealed class T123NotificationRecorder : IDisposable
{
    private readonly AlarmListViewModel _vm;
    private readonly List<T123Notification> _events = new();
    private readonly NotifyCollectionChangedEventHandler _alarmsHandler;
    private readonly NotifyCollectionChangedEventHandler _groupHandler;
    private readonly NotifyCollectionChangedEventHandler _ringingHandler;
    private readonly PropertyChangedEventHandler _propertyHandler;
    private readonly EventHandler _loadCommandHandler;
    private readonly EventHandler _toggleCommandHandler;
    private readonly EventHandler _deleteCommandHandler;
    private readonly EventHandler _dismissCommandHandler;
    private readonly EventHandler _dismissAllCommandHandler;

    public T123NotificationRecorder(AlarmListViewModel vm)
    {
        _vm = vm;
        _alarmsHandler = (_, e) => Add("Alarms", null, e.Action);
        _groupHandler = (_, e) => Add("GroupAlarms", null, e.Action);
        _ringingHandler = (_, e) => Add("RingingAlarms", null, e.Action);
        _propertyHandler = (_, e) => Add("vm", e.PropertyName, null);
        _loadCommandHandler = (_, _) => Add("cmd:Load", null, null);
        _toggleCommandHandler = (_, _) => Add("cmd:Toggle", null, null);
        _deleteCommandHandler = (_, _) => Add("cmd:Delete", null, null);
        _dismissCommandHandler = (_, _) => Add("cmd:Dismiss", null, null);
        _dismissAllCommandHandler = (_, _) => Add("cmd:DismissAll", null, null);

        vm.Alarms.CollectionChanged += _alarmsHandler;
        vm.GroupAlarms.CollectionChanged += _groupHandler;
        vm.RingingAlarms.CollectionChanged += _ringingHandler;
        vm.PropertyChanged += _propertyHandler;
        vm.LoadAlarmsCommand.CanExecuteChanged += _loadCommandHandler;
        vm.ToggleAlarmCommand.CanExecuteChanged += _toggleCommandHandler;
        vm.DeleteAlarmCommand.CanExecuteChanged += _deleteCommandHandler;
        vm.DismissRingingAlarmCommand.CanExecuteChanged += _dismissCommandHandler;
        vm.DismissAllRingingCommand.CanExecuteChanged += _dismissAllCommandHandler;
    }

    public IReadOnlyList<T123Notification> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }
    }

    public void AssertAllOnUiThread(int uiThreadId)
    {
        var offThread = Events.Where(e => e.ThreadId != uiThreadId).ToList();
        Assert.True(
            offThread.Count == 0,
            $"notifications raised off the UI thread ({offThread.Count}): " +
            string.Join("; ", offThread.Select(e => $"{e.Source}/{e.Property ?? e.Action?.ToString()}@tid={e.ThreadId}")) +
            $" (UI tid={uiThreadId})");
    }

    public void Dispose()
    {
        _vm.Alarms.CollectionChanged -= _alarmsHandler;
        _vm.GroupAlarms.CollectionChanged -= _groupHandler;
        _vm.RingingAlarms.CollectionChanged -= _ringingHandler;
        _vm.PropertyChanged -= _propertyHandler;
        _vm.LoadAlarmsCommand.CanExecuteChanged -= _loadCommandHandler;
        _vm.ToggleAlarmCommand.CanExecuteChanged -= _toggleCommandHandler;
        _vm.DeleteAlarmCommand.CanExecuteChanged -= _deleteCommandHandler;
        _vm.DismissRingingAlarmCommand.CanExecuteChanged -= _dismissCommandHandler;
        _vm.DismissAllRingingCommand.CanExecuteChanged -= _dismissAllCommandHandler;
    }

    private void Add(string source, string? property, NotifyCollectionChangedAction? action)
    {
        lock (_events)
        {
            _events.Add(new T123Notification(source, property, action, Environment.CurrentManagedThreadId));
        }
    }
}

internal readonly record struct T123Notification(
    string Source,
    string? Property,
    NotifyCollectionChangedAction? Action,
    int ThreadId);

/// <summary>Registration store whose read always finishes on a worker thread, like the real file store.</summary>
internal sealed class T123WorkerRegistrationStore : ICloudDeviceRegistrationStore
{
    private readonly CloudDeviceRegistration _registration;
    private int _loads;

    public T123WorkerRegistrationStore(CloudDeviceRegistration registration) => _registration = registration;

    public int LoadCount => Volatile.Read(ref _loads);

    public async Task<CloudDeviceRegistration?> LoadRegistrationAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loads);
        await Task.Run(static () => { }, cancellationToken).ConfigureAwait(false);
        return _registration;
    }

    public Task SaveRegistrationAsync(CloudDeviceRegistration registration, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ClearRegistrationAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Counts reads so a test can prove which projections really ran.</summary>
internal sealed class T123CountingGroupAlarmSource : IGroupAlarmSource
{
    private readonly IGroupAlarmSource _inner;
    private int _loads;

    public T123CountingGroupAlarmSource(IGroupAlarmSource inner) => _inner = inner;

    public int LoadCount => Volatile.Read(ref _loads);

    public Task<GroupAlarmSourceResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loads);
        return _inner.LoadAsync(cancellationToken);
    }
}

/// <summary>
/// Same shape as the read-only source, but one armed call parks inside <see cref="LoadAsync"/>. That is how
/// a test holds the projection gate at a known point without reaching into the view model's internals.
/// </summary>
internal sealed class T123GatedGroupAlarmSource : IGroupAlarmSource
{
    private readonly IGroupAlarmSource _inner;
    private readonly object _lock = new();
    private TaskCompletionSource<object?>? _entered;
    private TaskCompletionSource<object?>? _nextGate;
    private TaskCompletionSource<object?>? _activeGate;
    private int _loads;
    private int _blocked;

    public T123GatedGroupAlarmSource(IGroupAlarmSource inner) => _inner = inner;

    public int LoadCount => Volatile.Read(ref _loads);

    public int BlockedLoadCount => Volatile.Read(ref _blocked);

    public Task Entered
    {
        get
        {
            lock (_lock)
            {
                return _entered?.Task ?? Task.CompletedTask;
            }
        }
    }

    public void Arm()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release()
    {
        TaskCompletionSource<object?>? gate;
        lock (_lock)
        {
            gate = _activeGate ?? _nextGate;
            _activeGate = null;
            _nextGate = null;
        }

        gate?.TrySetResult(null);
    }

    public async Task<GroupAlarmSourceResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loads);

        Task? wait = null;
        TaskCompletionSource<object?>? entered = null;
        lock (_lock)
        {
            if (_nextGate is not null)
            {
                _activeGate = _nextGate;
                _nextGate = null;
                wait = _activeGate.Task;
                entered = _entered;
            }
        }

        if (wait is not null)
        {
            Interlocked.Increment(ref _blocked);
            entered?.TrySetResult(null);
            await wait.ConfigureAwait(false);
        }

        return await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Records OS registration calls and can park one call so a load stops at a known point.</summary>
internal sealed class T123RecordingAlarmScheduler : IAlarmScheduler
{
    private readonly object _lock = new();
    private readonly List<(string Id, DateTimeOffset Time)> _scheduled = new();
    private readonly List<string> _cancelled = new();
    private TaskCompletionSource<object?>? _holdGate;
    private TaskCompletionSource<object?>? _holdEntered;

    public int ScheduledCount
    {
        get
        {
            lock (_lock)
            {
                return _scheduled.Count;
            }
        }
    }

    public int CancelledCount
    {
        get
        {
            lock (_lock)
            {
                return _cancelled.Count;
            }
        }
    }

    public T123SchedulerHold Hold()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _holdEntered = entered;
            _holdGate = gate;
        }

        return new T123SchedulerHold(entered.Task, gate);
    }

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        Task? pending = null;
        TaskCompletionSource<object?>? entered = null;
        lock (_lock)
        {
            _scheduled.Add((alarm.Id, fireTime));
            if (_holdGate is not null)
            {
                pending = _holdGate.Task;
                entered = _holdEntered;
                _holdGate = null;
                _holdEntered = null;
            }
        }

        entered?.TrySetResult(null);
        return pending ?? Task.CompletedTask;
    }

    public Task CancelAlarmAsync(string alarmId)
    {
        lock (_lock)
        {
            _cancelled.Add(alarmId);
        }

        return Task.CompletedTask;
    }
}

internal sealed class T123SchedulerHold
{
    private readonly TaskCompletionSource<object?> _gate;

    public T123SchedulerHold(Task entered, TaskCompletionSource<object?> gate)
    {
        Entered = entered;
        _gate = gate;
    }

    public Task Entered { get; }

    public void Release() => _gate.TrySetResult(null);
}

/// <summary>Permission state the readiness check can be moved with between two loads.</summary>
internal sealed class T123PermissionService : IPermissionService
{
    public int OsApiLevel => 35;
    public bool CanScheduleExactAlarms { get; set; } = true;
    public bool HasNotificationPermission => true;
    public bool IsIgnoringBatteryOptimizations => true;
    public bool CanUseFullScreenIntent => true;

    public void RequestNotificationPermission() { }
    public void OpenExactAlarmSettings() { }
    public void OpenFullScreenIntentSettings() { }
    public void OpenBatteryOptimizationSettings() { }
}

/// <summary>Real store that can be told to fail one mutator, to prove a failure still releases IsBusy.</summary>
internal sealed class T123FailingMutationAlarmStore : IAlarmStore
{
    private readonly IAlarmStore _inner;

    public T123FailingMutationAlarmStore(IAlarmStore inner) => _inner = inner;

    public bool FailSaves { get; set; }

    public bool FailDeletes { get; set; }

    public Task<IReadOnlyList<LocalAlarmModel>> GetAllAsync() => _inner.GetAllAsync();

    public Task<LocalAlarmModel?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);

    public Task SaveAsync(LocalAlarmModel alarm) =>
        FailSaves ? throw new IOException("alarm store unavailable") : _inner.SaveAsync(alarm);

    public Task DeleteAsync(string id) =>
        FailDeletes ? throw new IOException("alarm store unavailable") : _inner.DeleteAsync(id);

    public Task<LocalAlarmModel?> ToggleEnabledAsync(string id) => _inner.ToggleEnabledAsync(id);

    public Task<LocalAlarmModel?> UpdateAsync(string id, Action<LocalAlarmModel> mutate) =>
        _inner.UpdateAsync(id, mutate);
}
