using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class T9BoundedDefectTests
{
    private sealed class RecordingAlarmScheduler : IAlarmScheduler
    {
        public List<(string Id, DateTimeOffset FireTime, bool IsSnoozeMatch)> Calls { get; } = new();

        public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
        {
            Calls.Add((alarm.Id, fireTime, alarm.SnoozeUntil.HasValue && alarm.SnoozeUntil.Value == fireTime));
            return Task.CompletedTask;
        }

        public Task CancelAlarmAsync(string alarmId) => Task.CompletedTask;
    }

    private sealed class TrackingDispatcher : IMainThreadDispatcher
    {
        public int DispatchCount { get; private set; }
        public bool IsDispatchRequired => true;

        public void Post(Action action)
        {
            DispatchCount++;
            action();
        }
    }

    private sealed class FailingScheduler : IAlarmScheduler
    {
        public Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime)
            => throw new InvalidOperationException("OS exact alarm scheduling failed (revoked permission).");

        public Task CancelAlarmAsync(string alarmId) => Task.CompletedTask;
    }

    // ==========================================
    // 1. Sound Option / Asset consistency tests
    // ==========================================

    [Theory]
    [InlineData("wakemesh://sound/builtin_chime", SoundOption.BuiltinChimeId)]
    [InlineData("builtin_chime", SoundOption.BuiltinChimeId)]
    [InlineData("os_default", SoundOption.DefaultOsId)]
    [InlineData(null, SoundOption.DefaultOsId)]
    [InlineData("", SoundOption.DefaultOsId)]
    [InlineData("   ", SoundOption.DefaultOsId)]
    [InlineData("custom://ringtone/sample", SoundOption.DefaultOsId)]
    public void Issue1_SoundOption_FromUri_MatchesConsistentEnumIds(string? uri, string expectedId)
    {
        var resolved = SoundOption.FromUri(uri);
        Assert.Equal(expectedId, resolved.Id);
    }

    [Fact]
    public void Issue1_ApkAsset_ContainsCorrectWakemeshChimeFilename()
    {
        var apkPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "WakeMesh.Mobile", "bin", "Debug", "net11.0-android", "com.companyname.wakemesh.mobile-Signed.apk");

        if (File.Exists(apkPath))
        {
            using var archive = ZipFile.OpenRead(apkPath);
            var chimeEntry = archive.GetEntry("assets/wakemesh_chime.wav");
            Assert.NotNull(chimeEntry);

            var oldMismatchEntry = archive.GetEntry("assets/builtin_chime.wav");
            Assert.Null(oldMismatchEntry);
        }
    }

    // ==========================================
    // 2. Snooze failure & active ring manager tests
    // ==========================================

    [Fact]
    public async Task Issue2_Snooze_WhenExecutionFails_MaintainsRingingItemInManager()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect2_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var scheduler = new FailingScheduler();
            var manager = new ActiveAlarmRingManager();
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(new LocalAlarmModel
            {
                Id = "a-fail",
                Name = "Fail Alarm",
                RepeatKind = AlarmRepeatKind.Weekly,
                IsEnabled = true,
                ActiveWeekdays = (Weekdays)127,
                Time = new TimeOnly(7, 0)
            });

            manager.AddRingingAlarm(new ActiveRingingAlarm("a-fail", "Fail Alarm", now, 5));
            Assert.True(manager.HasActiveAlarms);

            var result = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, scheduler, "a-fail", 5, now);
            Assert.False(result.Success);

            // On failure, Service does NOT call RingManager.SnoozeAlarm
            // Ringing alarm remains active in manager for resumed playback and actionable notification
            Assert.True(manager.HasActiveAlarms);
            Assert.Single(manager.GetRingingAlarms(), a => a.AlarmId == "a-fail");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Issue2_Snooze_WhenExecutionSucceeds_RemovesRingingItemFromManager()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect2_ok_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var scheduler = new RecordingAlarmScheduler();
            var manager = new ActiveAlarmRingManager();
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(new LocalAlarmModel
            {
                Id = "a-ok",
                Name = "OK Alarm",
                RepeatKind = AlarmRepeatKind.Weekly,
                IsEnabled = true,
                ActiveWeekdays = (Weekdays)127,
                Time = new TimeOnly(7, 0)
            });

            manager.AddRingingAlarm(new ActiveRingingAlarm("a-ok", "OK Alarm", now, 5));
            var result = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, scheduler, "a-ok", 5, now);
            Assert.True(result.Success);

            // On success, Service calls RingManager.SnoozeAlarm to complete snooze
            var snoozed = manager.SnoozeAlarm("a-ok");
            Assert.NotNull(snoozed);
            Assert.False(manager.HasActiveAlarms);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ==========================================
    // 3. Firing Decision & Reschedule tests
    // ==========================================

    [Fact]
    public async Task Issue3_WeeklySnoozeFire_CalculatesNextWeeklyOccurrenceInFuture_NotPastSnooze()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var scheduler = new RecordingAlarmScheduler();
            var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService(initialCalendar: null, cacheFilePath: "/nonexistent"));
            var t0 = DateTimeOffset.UtcNow;

            await store.SaveAsync(new LocalAlarmModel
            {
                Id = "w1",
                RepeatKind = AlarmRepeatKind.Weekly,
                IsEnabled = true,
                ActiveWeekdays = (Weekdays)127,
                Time = new TimeOnly(7, 0),
                ExcludeHolidays = false
            });

            // Snooze for 5 minutes
            var snoozeRes = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, scheduler, "w1", 5, t0);
            Assert.True(snoozeRes.Success);

            // Snooze fires at t0 + 5 minutes
            var fireAt = t0.AddMinutes(5);
            var nowAtFire = fireAt.AddSeconds(1);

            await AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync(
                store,
                calculator,
                scheduler,
                "w1",
                AlarmIntentHelper.ActionSnoozeTrigger,
                nowAtFire);

            var updated = await store.GetByIdAsync("w1");
            Assert.NotNull(updated);
            Assert.Null(updated.SnoozeUntil);

            var lastCall = Assert.Single(scheduler.Calls, c => c.FireTime > fireAt);
            Assert.True(lastCall.FireTime > nowAtFire, $"Expected future weekly schedule, got {lastCall.FireTime:O} <= {nowAtFire:O}");
            Assert.False(lastCall.IsSnoozeMatch);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Issue3_OnceSnoozeFire_DisablesAlarmAndClearsNextFireTime()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect3_once_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var scheduler = new RecordingAlarmScheduler();
            var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService(initialCalendar: null, cacheFilePath: "/nonexistent"));
            var t0 = DateTimeOffset.UtcNow;

            await store.SaveAsync(new LocalAlarmModel
            {
                Id = "once1",
                RepeatKind = AlarmRepeatKind.Once,
                IsEnabled = true,
                Time = new TimeOnly(8, 0)
            });

            // Snooze once alarm
            var snoozeRes = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, scheduler, "once1", 5, t0);
            Assert.True(snoozeRes.Success);

            var fireAt = t0.AddMinutes(5);
            var nowAtFire = fireAt.AddSeconds(1);

            await AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync(
                store,
                calculator,
                scheduler,
                "once1",
                AlarmIntentHelper.ActionSnoozeTrigger,
                nowAtFire);

            var updated = await store.GetByIdAsync("once1");
            Assert.NotNull(updated);
            Assert.Null(updated.SnoozeUntil);
            Assert.False(updated.IsEnabled);
            Assert.Null(updated.NextFireTime);
            Assert.Equal(AlarmRegistrationStatus.Unregistered, updated.OsRegistrationStatus);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Issue3_WeeklyNormalFire_CalculatesNextWeeklyOccurrenceInFuture()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect3_norm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var scheduler = new RecordingAlarmScheduler();
            var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService(initialCalendar: null, cacheFilePath: "/nonexistent"));
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(new LocalAlarmModel
            {
                Id = "w-norm",
                RepeatKind = AlarmRepeatKind.Weekly,
                IsEnabled = true,
                ActiveWeekdays = (Weekdays)127,
                Time = new TimeOnly(7, 0)
            });

            await AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync(
                store,
                calculator,
                scheduler,
                "w-norm",
                AlarmIntentHelper.ActionAlarmTrigger,
                now);

            var updated = await store.GetByIdAsync("w-norm");
            Assert.NotNull(updated);
            Assert.True(updated.NextFireTime.HasValue);
            Assert.True(updated.NextFireTime.Value > now);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ==========================================
    // 4. RingManager & VM Threading / Lifecycle tests
    // ==========================================

    [Fact]
    public void Issue4_DismissTimedOutAlarms_SubscriberExceptionDoesNotEscapeOrBlock()
    {
        var manager = new ActiveAlarmRingManager();
        var now = DateTimeOffset.UtcNow;
        manager.AddRingingAlarm(new ActiveRingingAlarm("a1", "Alarm 1", now.AddMinutes(-15), 5));

        bool handlerInvoked = false;
        EventHandler throwingHandler = (s, e) =>
        {
            handlerInvoked = true;
            throw new InvalidOperationException("Subscriber threw unexpected exception");
        };

        manager.RingingStateChanged += throwingHandler;
        try
        {
            // Requirement 4: 購読者例外がtimeout停止を妨げない (must not throw out of DismissTimedOutAlarms)
            var dismissed = manager.DismissTimedOutAlarms(now, TimeSpan.FromMinutes(10));
            Assert.True(handlerInvoked);
            Assert.Single(dismissed, "a1");
            Assert.False(manager.HasActiveAlarms);
        }
        finally
        {
            manager.RingingStateChanged -= throwingHandler;
        }
    }

    [Fact]
    public void Issue4_EventInvokedOutsideSyncLock_PreventsDeadlockOnReentrantAccess()
    {
        var manager = new ActiveAlarmRingManager();
        var now = DateTimeOffset.UtcNow;
        manager.AddRingingAlarm(new ActiveRingingAlarm("a-reenter", "Reentrant", now.AddMinutes(-15), 5));

        bool reenteredSuccessfully = false;
        EventHandler reentrantHandler = (s, e) =>
        {
            // Calling another lock-requiring method from within event handler
            // If the event was raised inside lock(_syncLock), reentering can cause deadlock on multi-thread
            var alarms = manager.GetRingingAlarms();
            reenteredSuccessfully = alarms != null;
        };

        manager.RingingStateChanged += reentrantHandler;
        try
        {
            manager.DismissTimedOutAlarms(now, TimeSpan.FromMinutes(10));
            Assert.True(reenteredSuccessfully);
        }
        finally
        {
            manager.RingingStateChanged -= reentrantHandler;
        }
    }

    [Fact]
    public async Task Issue4_AlarmListViewModel_DispatchesToMainThread_WhenWorkerThreadRaisesEvent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect4_vm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var holiday = new JapaneseHolidayService();
            var calc = new AlarmScheduleCalculator(holiday);
            var sched = new RecordingAlarmScheduler();
            var dev = new LocalDeviceInfoProvider(storageFilePath: Path.Combine(tempDir, "dev.json"));
            var coord = new AlarmCoordinator(store, calc, sched);

            var manager = new ActiveAlarmRingManager();
            var dispatcher = new TrackingDispatcher();

            using var vm = new AlarmListViewModel(
                coord,
                holiday,
                dev,
                manager,
                holidaySyncCoordinator: null,
                dispatcher: dispatcher);

            // Initially empty
            Assert.Empty(vm.RingingAlarms);

            // Worker thread adds an alarm
            await Task.Run(() =>
            {
                manager.AddRingingAlarm(new ActiveRingingAlarm("r1", "Ringing Alarm", DateTimeOffset.UtcNow, 5));
            });

            // Dispatcher must be called to update UI collection on main thread
            Assert.True(dispatcher.DispatchCount > 0);
            Assert.Single(vm.RingingAlarms, a => a.AlarmId == "r1");
            Assert.True(vm.IsRinging);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Issue4_AlarmListViewModel_SubscribeAndUnsubscribeLifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "t9_defect4_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonFileAlarmStore(Path.Combine(tempDir, "alarms.json"));
            var holiday = new JapaneseHolidayService();
            var calc = new AlarmScheduleCalculator(holiday);
            var sched = new RecordingAlarmScheduler();
            var dev = new LocalDeviceInfoProvider(storageFilePath: Path.Combine(tempDir, "dev.json"));
            var coord = new AlarmCoordinator(store, calc, sched);

            var manager = new ActiveAlarmRingManager();
            var dispatcher = new TrackingDispatcher();

            var vm = new AlarmListViewModel(
                coord,
                holiday,
                dev,
                manager,
                holidaySyncCoordinator: null,
                dispatcher: dispatcher);

            int initialDispatches = dispatcher.DispatchCount;

            // Unsubscribe (e.g. OnDisappearing)
            vm.UnsubscribeRingingEvents();

            manager.AddRingingAlarm(new ActiveRingingAlarm("r-unsub", "Unsubscribed", DateTimeOffset.UtcNow, 5));
            Assert.Equal(initialDispatches, dispatcher.DispatchCount);

            // Resubscribe (e.g. OnAppearing)
            vm.SubscribeRingingEvents();
            // Subscribing immediately triggers a sync
            Assert.True(dispatcher.DispatchCount > initialDispatches);
            Assert.Single(vm.RingingAlarms, a => a.AlarmId == "r-unsub");

            // Dispose cleans up subscription
            vm.Dispose();
            int dispatchesAfterDispose = dispatcher.DispatchCount;
            manager.DismissAlarm("r-unsub");
            Assert.Equal(dispatchesAfterDispose, dispatcher.DispatchCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Probe_DI_NonAndroidBranch_ResolvesAllViewModels_WithDispatcher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t9probe_di_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            sc.AddWakeMeshCoreServices(Path.Combine(dir, "a.json"), Path.Combine(dir, "d.txt"));
            sc.AddSingleton<IAlarmScheduler, StubAlarmScheduler>();
            sc.AddSingleton<IPermissionService, StubPermissionService>();
            sc.AddSingleton<IAlarmRingingService, StubAlarmRingingService>();
            sc.AddSingleton<IAudioPlaybackService, StubAudioPlaybackService>();
            sc.AddTransient<AlarmListViewModel>();
            sc.AddTransient<AlarmEditViewModel>();
            sc.AddTransient<DiagnosticsViewModel>();
            using var sp = sc.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            Assert.NotNull(sp.GetRequiredService<AlarmListViewModel>());
            Assert.NotNull(sp.GetRequiredService<AlarmEditViewModel>());
            Assert.NotNull(sp.GetRequiredService<DiagnosticsViewModel>());
            Assert.NotNull(sp.GetRequiredService<HolidayForegroundSyncCoordinator>());
            Assert.NotNull(sp.GetRequiredService<IMainThreadDispatcher>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
