using System;
using System.Collections.Generic;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class AlarmScheduleCalculatorTests
{
    private readonly JapaneseHolidayService _holidayService = new(holidays: new[] { new DateOnly(2026, 1, 1) });
    private readonly AlarmScheduleCalculator _calculator;

    public AlarmScheduleCalculatorTests()
    {
        _calculator = new AlarmScheduleCalculator(_holidayService);
    }

    [Fact]
    public void CalculateNextFire_WhenDisabled_ReturnsNull()
    {
        var alarm = new LocalAlarmModel
        {
            Id = "a1",
            Name = "テスト",
            IsEnabled = false,
            Time = new TimeOnly(7, 0),
            DeviceId = "dev-1"
        };

        var next = _calculator.CalculateNextFire(alarm, DateTimeOffset.Now);
        Assert.Null(next);
    }

    [Fact]
    public void CalculateNextFire_WeeklyAlarm_SkipsHolidayWhenHolidaysExist()
    {
        // 2026-01-01 (元日, 木曜日) 07:00
        // 元日を除外して次の木曜日 (2026-01-08 07:00) を返す
        var alarm = new LocalAlarmModel
        {
            Id = "a2",
            Name = "木曜アラーム",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Thursday,
            Time = new TimeOnly(7, 0),
            ExcludeHolidays = true,
            DeviceId = "dev-1",
            TimeZoneId = "Asia/Tokyo"
        };

        var tokyoTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        // 2025-12-31 20:00 JST から検索
        var baseTime = new DateTimeOffset(2025, 12, 31, 20, 0, 0, tokyoTz.BaseUtcOffset);

        var next = _calculator.CalculateNextFire(alarm, baseTime);

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 1, 8), next.LocalDate);
        Assert.Equal(new TimeOnly(7, 0), next.LocalTime);
    }

    [Fact]
    public void CalculateNextFire_WhenHolidayDataMissing_FiresEvenOnHoliday_PriorityFiring()
    {
        // 祝日データなしサービス
        var missingHolidayService = new JapaneseHolidayService(
            holidays: null,
            warningMessage: "祝日データ未取得警告");
        var calculator = new AlarmScheduleCalculator(missingHolidayService);

        var alarm = new LocalAlarmModel
        {
            Id = "a3",
            Name = "木曜アラーム",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Thursday,
            Time = new TimeOnly(7, 0),
            ExcludeHolidays = true,
            DeviceId = "dev-1",
            TimeZoneId = "Asia/Tokyo"
        };

        var tokyoTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var baseTime = new DateTimeOffset(2025, 12, 31, 20, 0, 0, tokyoTz.BaseUtcOffset);

        var next = calculator.CalculateNextFire(alarm, baseTime);

        Assert.NotNull(next);
        // 祝日データがないため 2026-01-01 に発火（起床優先！）
        Assert.Equal(new DateOnly(2026, 1, 1), next.LocalDate);
        Assert.Equal(new TimeOnly(7, 0), next.LocalTime);
    }
}
