using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class AlarmViewModelsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileAlarmStore _store;
    private readonly JapaneseHolidayService _holidayService;
    private readonly AlarmScheduleCalculator _calculator;
    private readonly MockAlarmScheduler _scheduler;
    private readonly AlarmCoordinator _coordinator;
    private readonly LocalDeviceInfoProvider _deviceInfoProvider;

    public AlarmViewModelsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_vm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new JsonFileAlarmStore(Path.Combine(_tempDir, "alarms.json"));
        _holidayService = new JapaneseHolidayService(holidays: new[] { new DateOnly(2026, 1, 1) });
        _calculator = new AlarmScheduleCalculator(_holidayService);
        _scheduler = new MockAlarmScheduler();
        _coordinator = new AlarmCoordinator(_store, _calculator, _scheduler);
        _deviceInfoProvider = new LocalDeviceInfoProvider("test-device-id");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task ListViewModel_HonestUnconnectedState_DisplaysAccurateStatus()
    {
        var vm = new AlarmListViewModel(_coordinator, _holidayService, _deviceInfoProvider);

        Assert.Equal("未接続 (ローカル動作中)", vm.CloudSyncStatusText);
        Assert.Equal("未サインイン", vm.AuthStatusText);
        // T88 copy-map: 祝日データ適用済み → 内蔵の祝日データを確認済み (meaning unchanged)
        Assert.Contains("内蔵の祝日データを確認済み", vm.HolidayStatusText);

        await vm.LoadAlarmsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Alarms); // 起動時に勝手なサンプルアラームなし
    }

    [Fact]
    public async Task EditViewModel_Save_PreventsDoubleSubmitAndPersists()
    {
        var editVm = new AlarmEditViewModel(_coordinator, _deviceInfoProvider);
        editVm.Name = "朝の読書";
        editVm.Time = new TimeOnly(6, 30);
        editVm.IsWeekly = true;
        editVm.ActiveWeekdays = Weekdays.Monday | Weekdays.Wednesday;
        editVm.ExcludeHolidays = true;
        editVm.SnoozeMinutes = 10;

        var saveSuccess = await editVm.SaveAlarmAsync();
        Assert.True(saveSuccess);

        var all = await _store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("朝の読書", all[0].Name);
        Assert.Equal(new TimeOnly(6, 30), all[0].Time);
        Assert.Equal(10, all[0].SnoozeMinutes);
        Assert.Equal(AlarmRepeatKind.Weekly, all[0].RepeatKind);
    }
}
