using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T105 regression for the saved-alarm ON→OFF flip (T103 root cause): the page Switch re-binds
/// IsToggled (false→true on first bind / cell reuse / reload) and raises Toggled without a user tap.
/// The old handler called the inverting ToggleAlarmAsync, so the saved true became false.
/// These tests exercise the real JsonFileAlarmStore and the recording MockAlarmScheduler against
/// AlarmListViewModel.SetAlarmEnabledFromUserAsync (seams (a)-(f) of the T103 fix spec).
/// </summary>
public sealed class T105AlarmToggleRegressionTests : IDisposable
{
    private const string AlarmId = "t105-alarm-1";

    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly AlarmListViewModel _vm;

    public T105AlarmToggleRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_t105_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "alarms.json");
        _store = new JsonFileAlarmStore(_filePath);
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, new AlarmScheduleCalculator(_holidayService), _scheduler);
        _vm = new AlarmListViewModel(_coordinator, _holidayService, new LocalDeviceInfoProvider("t105-device"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static LocalAlarmModel NewAlarm(bool isEnabled) => new()
    {
        Id = AlarmId,
        Name = "T105 朝",
        IsEnabled = isEnabled,
        Time = new TimeOnly(7, 0),
        RepeatKind = AlarmRepeatKind.Weekly,
        ActiveWeekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday,
        DeviceId = "t105-device",
        TimeZoneId = "Asia/Tokyo"
    };

    private async Task SeedAsync(bool isEnabled) => await _store.SaveAsync(NewAlarm(isEnabled));

    /// <summary>A fresh UI instance, exactly what a reload / first bind hands to the Switch.</summary>
    private async Task<LocalAlarmModel> UiModelAsync()
    {
        var stored = await _store.GetByIdAsync(AlarmId);
        Assert.NotNull(stored);
        return stored!;
    }

    private string StoreText() => File.ReadAllText(_filePath);

    private async Task<bool> StoredEnabledAsync()
    {
        var stored = await _store.GetByIdAsync(AlarmId);
        Assert.NotNull(stored);
        return stored!.IsEnabled;
    }

    // (a) 初回バインドの発火: 保存 true / モデル true / requested true → 完全な no-op
    [Fact]
    public async Task A_FirstBindFire_WithMatchingValues_WritesNothingAndStaysEnabled()
    {
        await SeedAsync(true);
        var model = await UiModelAsync();
        var before = StoreText();

        var changed = await _vm.SetAlarmEnabledFromUserAsync(model, true);

        Assert.False(changed);
        Assert.True(model.IsEnabled);
        Assert.True(await StoredEnabledAsync());
        Assert.Equal(before, StoreText());
        Assert.Empty(_scheduler.ScheduledAlarms);
        Assert.Empty(_scheduler.CancelledAlarmIds);
    }

    // (b) 同じ requested=true を 2 回 → 1 回目だけ実処理、2 回目は冪等 no-op
    [Fact]
    public async Task B_SameRequestedTrueTwice_IsIdempotent()
    {
        await SeedAsync(false);
        var model = await UiModelAsync();

        var first = await _vm.SetAlarmEnabledFromUserAsync(model, true);
        var schedulesAfterFirst = _scheduler.ScheduledAlarms.Count;
        var cancelsAfterFirst = _scheduler.CancelledAlarmIds.Count;
        var afterFirst = StoreText();

        var second = await _vm.SetAlarmEnabledFromUserAsync(model, true);

        Assert.True(first);
        Assert.False(second);
        Assert.True(model.IsEnabled);
        Assert.True(await StoredEnabledAsync());
        Assert.Equal(1, schedulesAfterFirst);
        Assert.Equal(0, cancelsAfterFirst);
        Assert.Single(_scheduler.ScheduledAlarms);
        Assert.Equal(cancelsAfterFirst, _scheduler.CancelledAlarmIds.Count);
        Assert.Equal(afterFirst, StoreText());
    }

    // (c) 保存 true / requested false → false + Cancel。続けて true → true + Schedule + NextFire
    [Fact]
    public async Task C_OffThenOn_CancelsThenSchedulesWithNextFire()
    {
        await SeedAsync(true);
        var model = await UiModelAsync();

        var off = await _vm.SetAlarmEnabledFromUserAsync(model, false);

        Assert.True(off);
        Assert.False(model.IsEnabled);
        Assert.False(await StoredEnabledAsync());
        Assert.Contains(AlarmId, _scheduler.CancelledAlarmIds);
        Assert.Empty(_scheduler.ScheduledAlarms);
        var storedOff = await _store.GetByIdAsync(AlarmId);
        Assert.Null(storedOff!.NextFireTime);

        var on = await _vm.SetAlarmEnabledFromUserAsync(model, true);

        Assert.True(on);
        Assert.True(model.IsEnabled);
        Assert.True(await StoredEnabledAsync());
        Assert.Contains(AlarmId, _scheduler.ScheduledAlarms.ConvertAll(s => s.Id));
        var storedOn = await _store.GetByIdAsync(AlarmId);
        Assert.NotNull(storedOn!.NextFireTime);
        Assert.Equal(storedOn.NextFireTime, model.NextFireTime);
    }

    // (d) 古いモデル: モデル true / 保存 false（サービスが鳴動後に無効化）/ requested false
    //     → 反転 API を通さず false のまま。ここが旧実装で true に戻っていた箇所。
    [Fact]
    public async Task D_StaleModelTrue_StoredFalse_RequestedFalse_DoesNotFlipToTrue()
    {
        await SeedAsync(false);
        var model = NewAlarm(true); // UI にだけ古い true が残っている状態
        var before = StoreText();

        var ok = await _vm.SetAlarmEnabledFromUserAsync(model, false);

        Assert.True(ok);
        Assert.False(model.IsEnabled);
        Assert.False(await StoredEnabledAsync());
        Assert.Equal(before, StoreText());
        Assert.Empty(_scheduler.ScheduledAlarms);
        Assert.Empty(_scheduler.CancelledAlarmIds);
    }

    // (e) IsBusy 中は拒否し、保存値を変えない
    [Fact]
    public async Task E_BusyGuard_ReturnsFalseAndLeavesStoreUntouched()
    {
        await SeedAsync(true);
        var model = await UiModelAsync();
        var before = StoreText();
        _vm.IsBusy = true;

        var ok = await _vm.SetAlarmEnabledFromUserAsync(model, false);

        Assert.False(ok);
        Assert.True(model.IsEnabled);
        Assert.True(await StoredEnabledAsync());
        Assert.Equal(before, StoreText());
        Assert.Empty(_scheduler.ScheduledAlarms);
        Assert.Empty(_scheduler.CancelledAlarmIds);
    }

    // (f) LoadAlarmsAsync 後の新しいオブジェクトで保存値と同じ requested → no-op
    [Fact]
    public async Task F_AfterReload_NewObjectWithSameValue_IsNoOp()
    {
        await SeedAsync(true);
        await _vm.LoadAlarmsAsync();

        Assert.Single(_vm.Alarms);
        _scheduler.ScheduledAlarms.Clear();
        _scheduler.CancelledAlarmIds.Clear();
        var before = StoreText();
        var reloaded = _vm.Alarms[0];
        Assert.True(reloaded.IsEnabled);

        var ok = await _vm.SetAlarmEnabledFromUserAsync(reloaded, true);

        Assert.False(ok);
        Assert.True(reloaded.IsEnabled);
        Assert.True(await StoredEnabledAsync());
        Assert.Equal(before, StoreText());
        Assert.Empty(_scheduler.ScheduledAlarms);
        Assert.Empty(_scheduler.CancelledAlarmIds);
    }
}
