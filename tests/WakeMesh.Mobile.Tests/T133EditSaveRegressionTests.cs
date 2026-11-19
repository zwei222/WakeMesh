using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T133: the edit page saves from a command on the UI thread, but its cloud-managed guard awaits the
/// real <see cref="GroupAlarmSource"/>, whose file/registration reads complete on a worker thread. With
/// <c>ConfigureAwait(false)</c> at that call site every later observable step (PropertyChanged, the save
/// callback, IsBusy=false) ran on that worker thread. These tests give the view model a real
/// single-threaded UI pump, the real <see cref="AlarmEditViewModel"/> and the real
/// <see cref="GroupAlarmSource"/> over a registration store that always finishes on a worker thread —
/// the production shape, not a synchronously completing double.
/// </summary>
public sealed class T133EditSaveRegressionTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.Parse("aaaa1111-2222-4333-8444-555566667777");

    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly T133RecordingScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly T123WorkerRegistrationStore _registrationStore;
    private readonly GroupAlarmSource _groupSource;
    private readonly T123UiPump _pump;

    public T133EditSaveRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t133_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new T133RecordingScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);

        _registrationStore = new T123WorkerRegistrationStore(new CloudDeviceRegistration(
            DeviceId, 1, "https://api.wakemesh.local", "owner-account", "Fold5"));
        _groupSource = new GroupAlarmSource(_registrationStore, _tempDir);

        _pump = new T123UiPump();
    }

    public void Dispose()
    {
        _pump.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // --- 1) the save's tail must stay on the UI thread ----------------------

    [Fact]
    public void Save_WithAWorkerCompletingGuard_KeepsNotificationsAndCallbackOnTheUiThread()
    {
        _pump.Run(async () =>
        {
            var vm = NewViewModel();
            vm.Name = "UIスレッド確認";
            vm.Time = new TimeOnly(6, 45);

            var events = new List<(string? Property, int ThreadId)>();
            var callbackThreads = new List<int>();
            vm.OnSaveSuccess = () => callbackThreads.Add(Environment.CurrentManagedThreadId);
            PropertyChangedEventHandler handler = (_, e) =>
            {
                lock (events) { events.Add((e.PropertyName, Environment.CurrentManagedThreadId)); }
            };

            vm.PropertyChanged += handler;
            bool saved;
            try
            {
                saved = await vm.SaveAlarmAsync();
            }
            finally
            {
                vm.PropertyChanged -= handler;
            }

            Assert.True(saved);
            Assert.True(
                _registrationStore.LoadCount > 0,
                "the cloud-managed guard never read the registration, so the worker hop was not exercised");

            var offThread = events.Where(e => e.ThreadId != _pump.ThreadId).ToList();
            Assert.True(
                offThread.Count == 0,
                "notifications raised off the UI thread: " +
                string.Join(", ", offThread.Select(e => $"{e.Property}@tid={e.ThreadId}")) +
                $" (UI tid={_pump.ThreadId})");

            Assert.Contains(events, e => e.Property == nameof(AlarmEditViewModel.IsBusy));
            Assert.Equal(new[] { _pump.ThreadId }, callbackThreads);
            Assert.False(vm.IsBusy);
        });
    }

    // --- 2) a cloud-owned alarm saves nothing and calls nothing ------------

    [Fact]
    public void CloudOwnedAlarm_SavesNothingAndInvokesNoCallback()
    {
        _pump.Run(async () =>
        {
            var vm = NewViewModel();
            var binding = new CloudSyncBinding("https://api.wakemesh.local", "owner-account", DeviceId, 1);
            vm.Id = SyncLocalAlarmIdentity.GetLocalAlarmId(binding, Guid.Parse("99999999-1111-4111-8111-999999999999"));
            vm.Name = "グループ朝";
            vm.Time = new TimeOnly(7, 0);

            var callbacks = 0;
            vm.OnSaveSuccess = () => callbacks++;

            Assert.False(await vm.SaveAlarmAsync());
            Assert.Equal(AlarmEditViewModel.NotEditableMessage, vm.ErrorMessage);
            Assert.Equal(0, callbacks);
            Assert.Empty(await _store.GetAllAsync());
            Assert.Empty(_scheduler.Scheduled);
            Assert.False(vm.IsBusy);
        });
    }

    // --- 3) the chosen time and options reach the model and the scheduler --

    [Fact]
    public void Save_PassesTheChosenTimeAndOptionsToTheModelAndTheScheduler()
    {
        _pump.Run(async () =>
        {
            var vm = NewViewModel();
            vm.Name = "夕方のアラーム";
            vm.Time = new TimeOnly(18, 0);
            vm.TimeZoneId = "Asia/Tokyo";
            vm.IsWeekly = true;
            vm.ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday
                | Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday;
            vm.SnoozeMinutes = 7;
            vm.VolumePercent = 65;
            vm.EnableVibration = false;
            vm.SelectedSound = SoundOption.BuiltinChime;

            Assert.True(await vm.SaveAlarmAsync());

            var stored = await _store.GetByIdAsync(vm.Id);
            Assert.NotNull(stored);
            Assert.Equal(new TimeOnly(18, 0), stored!.Time);
            Assert.Equal(7, stored.SnoozeMinutes);
            Assert.Equal(65, stored.VolumePercent);
            Assert.False(stored.EnableVibration);
            Assert.Equal(SoundOption.BuiltinChime.Uri, stored.SoundUri);
            Assert.Equal("Asia/Tokyo", stored.TimeZoneId);

            var scheduled = Assert.Single(_scheduler.Scheduled);
            Assert.Equal(vm.Id, scheduled.Id);
            var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
            var localFire = TimeZoneInfo.ConvertTime(scheduled.FireTime, tokyo);
            Assert.Equal(new TimeOnly(18, 0), TimeOnly.FromDateTime(localFire.DateTime));
        });
    }

    // --- 4) an awaited navigation failure is a reported error, not async void --

    [Fact]
    public void AwaitableCallbackThrowingAsynchronously_IsReportedAndNeverEscapesTheSave()
    {
        _pump.Run(async () =>
        {
            var vm = NewViewModel();
            vm.Name = "遷移失敗";
            vm.Time = new TimeOnly(7, 15);

            var callbackRuns = 0;
            var callbackThread = 0;
            vm.OnSaveSuccessAsync = async () =>
            {
                Interlocked.Increment(ref callbackRuns);
                callbackThread = Environment.CurrentManagedThreadId;
                await Task.Run(static () => { });   // the failure arrives after a real await, as navigation does
                throw new InvalidOperationException("navigation stack unavailable");
            };

            var saved = await vm.SaveAlarmAsync();   // never throws: the exception is caught inside the save

            Assert.False(saved);
            Assert.Equal(1, callbackRuns);
            Assert.Equal(_pump.ThreadId, callbackThread);
            Assert.False(vm.IsBusy);
            Assert.NotNull(vm.ErrorMessage);
            Assert.Contains("navigation stack unavailable", vm.ErrorMessage);

            // the alarm was written before the navigation step failed: nothing is half-applied
            Assert.NotNull(await _store.GetByIdAsync(vm.Id));
            Assert.Single(_scheduler.Scheduled);
        });
    }

    // --- 5) the save task covers the callback, so a double tap cannot save twice --

    [Fact]
    public void SaveTask_DoesNotCompleteBeforeTheCallback_AndASecondTapIsRejected()
    {
        _pump.Run(async () =>
        {
            var vm = NewViewModel();
            vm.Name = "連打";
            vm.Time = new TimeOnly(7, 30);

            var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackRuns = 0;
            vm.OnSaveSuccessAsync = async () =>
            {
                Interlocked.Increment(ref callbackRuns);
                entered.TrySetResult(null);
                await gate.Task;
            };

            var save = vm.SaveAlarmAsync();
            await entered.Task;   // the save is parked inside the callback's await

            var first = await Task.WhenAny(save, Task.Delay(500));
            Assert.NotSame(save, first);   // still pending: the callback is part of the save
            Assert.True(vm.IsBusy);
            Assert.False(await vm.SaveAlarmAsync());

            gate.SetResult(null);
            Assert.True(await save);
            Assert.False(vm.IsBusy);
            Assert.Equal(1, callbackRuns);
            Assert.Single(_scheduler.Scheduled);
            Assert.Single(await _store.GetAllAsync());
        });
    }

    // --- 6) the sync callback stays compatible and never fires twice ---------

    [Fact]
    public void LegacySyncCallback_RunsExactlyOnce_AndNeverAlongsideTheAwaitableOne()
    {
        _pump.Run(async () =>
        {
            var syncRuns = 0;
            var asyncRuns = 0;

            var legacy = NewViewModel();
            legacy.Name = "旧コールバック";
            legacy.OnSaveSuccess = () => syncRuns++;
            Assert.True(await legacy.SaveAlarmAsync());
            Assert.Equal(1, syncRuns);
            Assert.Equal(0, asyncRuns);

            var both = NewViewModel();
            both.Name = "両方設定";
            both.OnSaveSuccess = () => syncRuns++;
            both.OnSaveSuccessAsync = () => { asyncRuns++; return Task.CompletedTask; };
            Assert.True(await both.SaveAlarmAsync());
            Assert.Equal(1, syncRuns);   // the awaitable callback wins; no second navigation
            Assert.Equal(1, asyncRuns);
            Assert.Equal(2, (await _store.GetAllAsync()).Count);
        });
    }

    private AlarmEditViewModel NewViewModel() => new(
        _coordinator,
        new LocalDeviceInfoProvider("t133-device"),
        audioPlaybackService: null,
        groupSource: _groupSource);
}

// --- dedicated T133 double: records the OS registration requests ------------

internal sealed class T133RecordingScheduler : IAlarmScheduler
{
    private readonly object _lock = new();
    private readonly List<(string Id, DateTimeOffset FireTime)> _scheduled = new();
    private readonly List<string> _cancelled = new();

    public IReadOnlyList<(string Id, DateTimeOffset FireTime)> Scheduled
    {
        get
        {
            lock (_lock)
            {
                return _scheduled.ToList();
            }
        }
    }

    public IReadOnlyList<string> Cancelled
    {
        get
        {
            lock (_lock)
            {
                return _cancelled.ToList();
            }
        }
    }

    public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
    {
        lock (_lock)
        {
            _scheduled.Add((alarm.Id, fireTime));
        }

        return Task.CompletedTask;
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
