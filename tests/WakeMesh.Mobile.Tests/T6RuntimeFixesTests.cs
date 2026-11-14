using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class T6RuntimeFixesTests : IDisposable
{
    private readonly string _tempDir;

    public T6RuntimeFixesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "t6_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // 1. DI ambiguous constructor resolution & Maui shared registration
    [Fact]
    public void T6_1_DI_Resolves_JapaneseHolidayService_DirectAndShared()
    {
        // Direct type registration without factory (tests ambiguous constructor resolution)
        var sc1 = new ServiceCollection();
        sc1.AddSingleton<IHolidayService, JapaneseHolidayService>();
        var sp1 = sc1.BuildServiceProvider();
        var svc1 = sp1.GetRequiredService<IHolidayService>();
        Assert.NotNull(svc1);

        // Shared registration with explicit factory
        var sc2 = new ServiceCollection();
        sc2.AddWakeMeshCoreServices(
            alarmsPath: Path.Combine(_tempDir, "alarms.json"),
            deviceIdPath: Path.Combine(_tempDir, "device_id.txt"));
        var sp2 = sc2.BuildServiceProvider();
        var svc2 = sp2.GetRequiredService<IHolidayService>();
        Assert.NotNull(svc2);
    }

    // 2. Pure decision + runtime orchestration: corrupted store prioritizes wake-up
    [Fact]
    public void T6_2_AlarmFiringDecision_CorruptedStore_PrioritizesWakeUp_DoesNotSuppress()
    {
        var now = DateTimeOffset.UtcNow;

        // When store read failed (e.g. corruption / IO error), MUST NOT suppress ringing!
        var decision = AlarmFiringDecisionHelper.Decide(
            AlarmIntentHelper.ActionAlarmTrigger,
            alarm: null,
            storeReadSucceeded: false,
            now: now);

        Assert.Equal(FiringDecision.Ring, decision);
    }

    [Fact]
    public void T6_2_AlarmFiringDecision_SuppressesOnlyWhenConfirmedDeletedOrDisabled()
    {
        var now = DateTimeOffset.UtcNow;

        // Confirmed deleted (read succeeded, alarm is null) -> Suppress
        var decisionDeleted = AlarmFiringDecisionHelper.Decide(
            AlarmIntentHelper.ActionAlarmTrigger,
            alarm: null,
            storeReadSucceeded: true,
            now: now);
        Assert.Equal(FiringDecision.Suppress, decisionDeleted);

        // Confirmed disabled without active snooze -> Suppress
        var disabledAlarm = new LocalAlarmModel { Id = "d1", IsEnabled = false, SnoozeUntil = null };
        var decisionDisabled = AlarmFiringDecisionHelper.Decide(
            AlarmIntentHelper.ActionAlarmTrigger,
            alarm: disabledAlarm,
            storeReadSucceeded: true,
            now: now);
        Assert.Equal(FiringDecision.Suppress, decisionDisabled);

        // Normal active alarm -> Ring
        var activeAlarm = new LocalAlarmModel { Id = "a1", IsEnabled = true };
        var decisionActive = AlarmFiringDecisionHelper.Decide(
            AlarmIntentHelper.ActionAlarmTrigger,
            alarm: activeAlarm,
            storeReadSucceeded: true,
            now: now);
        Assert.Equal(FiringDecision.Ring, decisionActive);

        // Snooze trigger on disabled alarm (e.g. one-shot that fired then snoozed) -> Ring!
        var snoozedOnceAlarm = new LocalAlarmModel
        {
            Id = "s1",
            IsEnabled = false,
            SnoozeUntil = now.AddMinutes(5)
        };
        var decisionSnooze = AlarmFiringDecisionHelper.Decide(
            AlarmIntentHelper.ActionSnoozeTrigger,
            alarm: snoozedOnceAlarm,
            storeReadSucceeded: true,
            now: now);
        Assert.Equal(FiringDecision.Ring, decisionSnooze);
    }

    // 3. Store static gate: 40 concurrent saves across multiple store instances lose no alarm
    [Fact]
    public async Task T6_3_Store_40ConcurrentSaves_AcrossMultipleInstances_LosesNoAlarm()
    {
        var path = Path.Combine(_tempDir, "concurrent_alarms.json");
        const int count = 40;
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            try
            {
                var store = new JsonFileAlarmStore(path);
                await store.SaveAsync(new LocalAlarmModel { Id = "alarm-" + i, Name = "Alarm " + i });
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);
        Assert.Empty(errors);

        var finalStore = new JsonFileAlarmStore(path);
        var all = await finalStore.GetAllAsync();
        Assert.Equal(count, all.Count);
    }

    [Fact]
    public async Task T6_3_Store_UpdateAsync_MutatesAtomically()
    {
        var path = Path.Combine(_tempDir, "update_alarms.json");
        var store = new JsonFileAlarmStore(path);
        await store.SaveAsync(new LocalAlarmModel { Id = "a1", Name = "Original", VolumePercent = 50 });

        var updated = await store.UpdateAsync("a1", a =>
        {
            a.VolumePercent = 90;
            a.SnoozeUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        });

        Assert.NotNull(updated);
        Assert.Equal(90, updated.VolumePercent);
        Assert.NotNull(updated.SnoozeUntil);

        var retrieved = await store.GetByIdAsync("a1");
        Assert.NotNull(retrieved);
        Assert.Equal(90, retrieved.VolumePercent);
        Assert.Equal("Original", retrieved.Name);
    }

    // 4. One-shot alarm fired then snoozed survives RescheduleAllActiveAsync & SaveAndSchedule
    [Fact]
    public async Task T6_4_OnceAlarm_FiredThenSnoozed_SurvivesRescheduleAll()
    {
        var store = new JsonFileAlarmStore(Path.Combine(_tempDir, "p3_alarms.json"));
        var sched = new RecordingScheduler();
        var coord = new AlarmCoordinator(store, new AlarmScheduleCalculator(new JapaneseHolidayService(initialCalendar: null, cacheFilePath: "/nonexistent")), sched);

        var snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.SaveAsync(new LocalAlarmModel
        {
            Id = "once",
            RepeatKind = AlarmRepeatKind.Once,
            IsEnabled = false,
            SnoozeUntil = snoozeUntil,
            NextFireTime = snoozeUntil
        });

        await coord.RescheduleAllActiveAsync();

        Assert.Contains("schedule:once", sched.Ops);
    }

    [Fact]
    public async Task T6_4_AlarmEditViewModel_PreservesSnoozeOnEdit_ClearsOnExplicitDisable()
    {
        var store = new JsonFileAlarmStore(Path.Combine(_tempDir, "edit_snooze.json"));
        var sched = new RecordingScheduler();
        var coord = new AlarmCoordinator(store, new AlarmScheduleCalculator(new JapaneseHolidayService(initialCalendar: null, cacheFilePath: "/nonexistent")), sched);
        var dev = new LocalDeviceInfoProvider(storageFilePath: Path.Combine(_tempDir, "dev.txt"));

        var futureSnooze = DateTimeOffset.UtcNow.AddMinutes(8);
        var alarm = new LocalAlarmModel
        {
            Id = "edit-me",
            Name = "Initial",
            IsEnabled = true,
            SnoozeUntil = futureSnooze,
            Time = new TimeOnly(7, 30)
        };
        await store.SaveAsync(alarm);

        // Edit without changing IsEnabled: snooze must be preserved
        var vm = new AlarmEditViewModel(coord, dev);
        vm.LoadFrom(alarm);
        vm.Name = "Updated Name";
        await vm.SaveAlarmAsync();

        var stored = await store.GetByIdAsync("edit-me");
        Assert.NotNull(stored);
        Assert.Equal(futureSnooze, stored.SnoozeUntil);

        // Edit and explicitly disable: snooze must be cancelled
        vm.IsEnabled = false;
        await vm.SaveAlarmAsync();

        stored = await store.GetByIdAsync("edit-me");
        Assert.NotNull(stored);
        Assert.False(stored.IsEnabled);
        Assert.Null(stored.SnoozeUntil);
    }

    // 5. Service snooze orchestration: await save and schedule, maintain actionable failure state
    [Fact]
    public async Task T6_5_SnoozeExecution_AwaitsSaveAndSchedule_ReturnsSuccessAndUpdatesStore()
    {
        var store = new JsonFileAlarmStore(Path.Combine(_tempDir, "snooze_ok.json"));
        var sched = new RecordingScheduler();
        var alarm = new LocalAlarmModel
        {
            Id = "snooze-ok",
            Name = "Snooze Test",
            IsEnabled = true,
            Time = new TimeOnly(8, 0)
        };
        await store.SaveAsync(alarm);

        var now = DateTimeOffset.UtcNow;
        var result = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, sched, "snooze-ok", 5, now);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);

        var updated = await store.GetByIdAsync("snooze-ok");
        Assert.NotNull(updated);
        Assert.Equal(now.AddMinutes(5), updated.SnoozeUntil);
        Assert.Equal(AlarmRegistrationStatus.Scheduled, updated.OsRegistrationStatus);
        Assert.Contains("schedule:snooze-ok", sched.Ops);
    }

    [Fact]
    public async Task T6_5_SnoozeExecution_WhenSchedulerFails_MarksFailedStatus_ReturnsFailure_NoSilentSuccess()
    {
        var store = new JsonFileAlarmStore(Path.Combine(_tempDir, "snooze_fail.json"));
        var failingSched = new FailingScheduler();
        var alarm = new LocalAlarmModel
        {
            Id = "snooze-fail",
            Name = "Failing Snooze",
            IsEnabled = true,
            Time = new TimeOnly(8, 0)
        };
        await store.SaveAsync(alarm);

        var now = DateTimeOffset.UtcNow;
        var result = await AlarmFiringDecisionHelper.ExecuteSnoozeAsync(store, failingSched, "snooze-fail", 5, now);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);

        var updated = await store.GetByIdAsync("snooze-fail");
        Assert.NotNull(updated);
        Assert.Equal(AlarmRegistrationStatus.Failed, updated.OsRegistrationStatus);
        Assert.Equal("Exact alarm scheduling rejected", updated.OsRegistrationError);
    }

    // 6. RingManager timeout per alarm: later alarm is not stopped early
    [Fact]
    public void T6_6_RingManager_DismissesTimedOutAlarmsIndividually_DoesNotStopLaterAlarmsEarly()
    {
        var manager = new ActiveAlarmRingManager();
        var t0 = DateTimeOffset.UtcNow;

        // Alarm 1 starts at t0
        var alarm1 = new ActiveRingingAlarm("a1", "Alarm 1", t0, 5);
        manager.AddRingingAlarm(alarm1);

        // Alarm 2 starts at t0 + 5 minutes
        var alarm2 = new ActiveRingingAlarm("a2", "Alarm 2", t0.AddMinutes(5), 5);
        manager.AddRingingAlarm(alarm2);

        // At t0 + 10 minutes: Alarm 1 reaches 10min limit, but Alarm 2 has only been ringing for 5min!
        var timedOutAt10Min = manager.DismissTimedOutAlarms(t0.AddMinutes(10), ActiveAlarmRingManager.MaxRingingDuration);

        Assert.Contains("a1", timedOutAt10Min);
        Assert.DoesNotContain("a2", timedOutAt10Min);

        // Alarm 2 MUST still be ringing!
        Assert.True(manager.HasActiveAlarms);
        var remaining = manager.GetRingingAlarms();
        Assert.Single(remaining);
        Assert.Equal("a2", remaining[0].AlarmId);

        // At t0 + 15 minutes: Alarm 2 reaches 10min limit
        var timedOutAt15Min = manager.DismissTimedOutAlarms(t0.AddMinutes(15), ActiveAlarmRingManager.MaxRingingDuration);
        Assert.Contains("a2", timedOutAt15Min);
        Assert.False(manager.HasActiveAlarms);
    }

    [Fact]
    public void T6_6_RingManager_DismissAll_CompletelyClearsState()
    {
        var manager = new ActiveAlarmRingManager();
        manager.AddRingingAlarm(new ActiveRingingAlarm("a1", "Alarm 1", DateTimeOffset.UtcNow, 5));
        manager.AddRingingAlarm(new ActiveRingingAlarm("a2", "Alarm 2", DateTimeOffset.UtcNow, 5));
        Assert.True(manager.HasActiveAlarms);

        manager.DismissAll();
        Assert.False(manager.HasActiveAlarms);
        Assert.Empty(manager.GetRingingAlarms());

        // Subsequent alarm can now start cleanly as first alarm
        var isFirst = manager.AddRingingAlarm(new ActiveRingingAlarm("a3", "Alarm 3", DateTimeOffset.UtcNow, 5));
        Assert.True(isFirst);
    }

    private sealed class RecordingScheduler : IAlarmScheduler
    {
        public List<string> Ops { get; } = new();
        public Task ScheduleAlarmAsync(LocalAlarmModel a, DateTimeOffset t) { Ops.Add("schedule:" + a.Id); return Task.CompletedTask; }
        public Task CancelAlarmAsync(string id) { Ops.Add("cancel:" + id); return Task.CompletedTask; }
    }

    private sealed class FailingScheduler : IAlarmScheduler
    {
        public Task ScheduleAlarmAsync(LocalAlarmModel a, DateTimeOffset t) => throw new InvalidOperationException("Exact alarm scheduling rejected");
        public Task CancelAlarmAsync(string id) => Task.CompletedTask;
    }
}
