using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// JPC-APP wiring checks: DI and both Android receivers consume the bundled calendar; the legacy
/// cache file is inert; the refresh action re-checks bundled data and re-registers alarms.
/// </summary>
public sealed class OfflineHolidayWiringTests : IDisposable
{
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);
    private static readonly Weekdays EveryDay =
        Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday |
        Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday;

    private static readonly string RepoRoot = FindRepoRoot();

    private readonly string _tempDir;

    public OfflineHolidayWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_jpc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            // best effort
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static DateTimeOffset JstAt(DateOnly date, TimeOnly time) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, Jst);

    private string AlarmsPath => Path.Combine(_tempDir, "alarms.json");

    private JsonFileAlarmStore NewStore() => new(AlarmsPath);

    private LocalAlarmModel WeeklyAlarm(string id = "alarm-1") => new()
    {
        Id = id,
        Name = "週次アラーム",
        IsEnabled = true,
        RepeatKind = AlarmRepeatKind.Weekly,
        ActiveWeekdays = EveryDay,
        Time = new TimeOnly(7, 0),
        ExcludeHolidays = true,
        DeviceId = "dev-1",
        TimeZoneId = "Asia/Tokyo"
    };

    [Fact]
    public void DiRegistration_ResolvesBundledHolidayData()
    {
        var services = new ServiceCollection();
        services.AddWakeMeshCoreServices(
            alarmsPath: AlarmsPath,
            deviceIdPath: Path.Combine(_tempDir, "device_id.txt"));

        using var provider = services.BuildServiceProvider();
        var holiday = provider.GetRequiredService<IHolidayService>();

        Assert.True(holiday.HasHolidayData);
        Assert.False(holiday.NeedsRefresh);
        Assert.Contains(new DateOnly(2026, 11, 23), holiday.GetHolidays());
        Assert.DoesNotContain(new DateOnly(2026, 11, 24), holiday.GetHolidays());
        Assert.Contains("内蔵祝日データ", holiday.HolidayStatusSummary);
    }

    [Fact]
    public async Task RefreshAction_RecheckBundledData_RecalculatesAndReRegisters()
    {
        var store = NewStore();
        var scheduler = new MockAlarmScheduler();
        var holiday = JapaneseHolidayService.CreateBundled();
        var coordinator = new AlarmCoordinator(store, new AlarmScheduleCalculator(holiday), scheduler);
        await coordinator.SaveAlarmAsync(WeeklyAlarm());
        scheduler.ScheduledAlarms.Clear();

        var deviceInfo = new LocalDeviceInfoProvider(storageFilePath: Path.Combine(_tempDir, "device_id.txt"));
        var vm = new DiagnosticsViewModel(new MockPermissionService(), holiday, deviceInfo, coordinator);

        await vm.RefreshHolidaysCommand.ExecuteAsync(null);

        Assert.Equal("祝日データを確認し、1 件のアラームを再計算・再登録しました。", vm.HolidayOperationMessage);
        Assert.Single(scheduler.ScheduledAlarms);
        Assert.Equal("alarm-1", scheduler.ScheduledAlarms[0].Id);
    }

    [Fact]
    public void DiagnosticsCopy_StatesBundledSourceAndHonestCoverage()
    {
        var holiday = JapaneseHolidayService.CreateBundled();
        var vm = new DiagnosticsViewModel(new MockPermissionService(), holiday);

        Assert.Contains("内蔵祝日データ", vm.HolidayStatus);
        Assert.Contains("JpCalendar 2.0.0", vm.HolidayCoverageNotice);
        Assert.Contains("1955/01/01", vm.HolidayCoverageNotice);
        Assert.Contains("2027/11/23", vm.HolidayCoverageNotice);
        Assert.Contains("カバレッジ外", vm.HolidayCoverageNotice);
        Assert.Contains("祝日もアラーム対象", vm.HolidayCoverageNotice);
        Assert.DoesNotContain("内閣府", vm.HolidayCoverageNotice);
        Assert.DoesNotContain("取得", vm.HolidayCoverageNotice);
    }

    [Fact]
    public async Task LegacyCacheFile_IsNeverReadOrMutated()
    {
        var cachePath = Path.Combine(_tempDir, "wakemesh_holidays.csv");
        var original = Encoding.UTF8.GetBytes(
            "国民の祝日・休日月日,国民の祝日・休日名称\r\n2026/1/1,元日\r\n");
        await File.WriteAllBytesAsync(cachePath, original);

        // A null initial calendar still means "no data": the legacy cache must not fill the gap.
        var withoutData = new JapaneseHolidayService(initialCalendar: null, cacheFilePath: cachePath);
        Assert.False(withoutData.HasHolidayData);

        // Refresh only re-checks the bundle; it must not rewrite or migrate the cache file.
        Assert.True(await withoutData.RefreshHolidaysAsync());
        Assert.True(withoutData.HasHolidayData);

        Assert.Equal(original, await File.ReadAllBytesAsync(cachePath));

        var injected = new JapaneseHolidayService(
            holidays: new[] { new DateOnly(2026, 1, 1) },
            cacheFilePath: cachePath);
        Assert.True(await injected.RefreshHolidaysAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(cachePath));
    }

    [Fact]
    public void EmptyLegacyCache_DoesNotProvideHolidayData()
    {
        var cachePath = Path.Combine(_tempDir, "empty_holidays.csv");
        File.WriteAllBytes(cachePath, Array.Empty<byte>());

        var service = new JapaneseHolidayService(initialCalendar: null, cacheFilePath: cachePath);

        Assert.False(service.HasHolidayData);
        Assert.Equal(0, new FileInfo(cachePath).Length);
    }

    [Fact]
    public void JapaneseHolidayService_HasNoNetworkOrCacheWritePath()
    {
        var source = Read("src/WakeMesh.Mobile/Services/JapaneseHolidayService.cs");

        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("GetAsync", source);
        Assert.DoesNotContain("https://", source);
        Assert.DoesNotContain("www8.cao.go.jp", source);
        Assert.DoesNotContain("File.WriteAllBytes", source);
        Assert.DoesNotContain("File.Move", source);
        Assert.DoesNotContain("RefreshFromOfficial", source);
        Assert.Contains("BundledHolidayCalendar", source);
        Assert.Contains("public bool NeedsRefresh => false;", source);
    }

    [Fact]
    public void AndroidReceivers_UseBundledDataWithoutHolidaySuppression()
    {
        var alarmReceiver = Read("src/WakeMesh.Mobile/Platforms/Android/WakeMeshAlarmReceiver.cs");
        var bootReceiver = Read("src/WakeMesh.Mobile/Platforms/Android/WakeMeshBootReceiver.cs");

        Assert.Contains("JapaneseHolidayService.CreateBundled()", alarmReceiver);
        Assert.DoesNotContain("IsHoliday", alarmReceiver);
        Assert.DoesNotContain("HasHolidayData", alarmReceiver);
        Assert.DoesNotContain("GetHolidayCacheFilePath", alarmReceiver);
        // The existing ring-first decision and post-fire reschedule stay in place.
        Assert.Contains("AlarmFiringDecisionHelper.Decide", alarmReceiver);
        Assert.Contains("AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync", alarmReceiver);

        Assert.Contains("JapaneseHolidayService.CreateBundled()", bootReceiver);
        Assert.Contains("MY_PACKAGE_REPLACED", bootReceiver);
        Assert.DoesNotContain("IsHoliday", bootReceiver);
        Assert.DoesNotContain("HasHolidayData", bootReceiver);
        Assert.DoesNotContain("GetHolidayCacheFilePath", bootReceiver);
        Assert.Contains("RescheduleAllActiveAsync", bootReceiver);
    }

    [Fact]
    public async Task PostFireReschedule_WithFixedClock_SkipsHolidayAndKeepsAlarmIdentity()
    {
        var store = NewStore();
        var scheduler = new MockAlarmScheduler();
        var holiday = JapaneseHolidayService.CreateBundled();
        var calculator = new AlarmScheduleCalculator(holiday);
        var alarm = WeeklyAlarm();
        await store.SaveAsync(alarm);

        var now = JstAt(new DateOnly(2026, 11, 22), new TimeOnly(8, 0));
        await AlarmFiringDecisionHelper.HandlePostFireRescheduleAsync(
            store, calculator, scheduler, alarm.Id, AlarmIntentHelper.ActionAlarmTrigger, now);

        var updated = await store.GetByIdAsync(alarm.Id);
        Assert.NotNull(updated);
        Assert.Equal("alarm-1", updated!.Id);
        Assert.Equal(JstAt(new DateOnly(2026, 11, 24), new TimeOnly(7, 0)), updated.NextFireTime);
        Assert.Equal(AlarmRegistrationStatus.Scheduled, updated.OsRegistrationStatus);
        Assert.Single(scheduler.ScheduledAlarms, s => s.Id == "alarm-1");
    }

    [Fact]
    public async Task RescheduleAll_PreservesActiveSnoozeIdentityAndTime()
    {
        var store = NewStore();
        var scheduler = new MockAlarmScheduler();
        var holiday = JapaneseHolidayService.CreateBundled();
        var coordinator = new AlarmCoordinator(store, new AlarmScheduleCalculator(holiday), scheduler);

        var snoozeUntil = DateTimeOffset.UtcNow.AddMinutes(7);
        var alarm = WeeklyAlarm();
        alarm.SnoozeUntil = snoozeUntil;
        await store.SaveAsync(alarm);

        await coordinator.RescheduleAllActiveAsync();

        var scheduled = Assert.Single(scheduler.ScheduledAlarms);
        Assert.Equal("alarm-1", scheduled.Id);
        Assert.Equal(snoozeUntil, scheduled.Time);

        var stored = await store.GetByIdAsync("alarm-1");
        Assert.Equal(snoozeUntil, stored!.SnoozeUntil);
    }
}
