using System;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// JPC-APP: holiday skipping must work with the data bundled in the app, without any network
/// fetch and without depending on a previously downloaded cache file.
/// </summary>
public sealed class OfflineHolidaySchedulingTests
{
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    private static readonly Weekdays EveryDay =
        Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday |
        Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday;

    private static LocalAlarmModel AlarmAt(TimeOnly time, DateOnly? once = null) => new()
    {
        Id = "offline-alarm",
        Name = "Offline holiday alarm",
        IsEnabled = true,
        RepeatKind = once is null ? AlarmRepeatKind.Weekly : AlarmRepeatKind.Once,
        ActiveWeekdays = once is null ? EveryDay : Weekdays.None,
        Time = time,
        ExcludeHolidays = true,
        DeviceId = "dev-1",
        TimeZoneId = "Asia/Tokyo",
        OnceOnDate = once
    };

    private static DateTimeOffset JstAt(DateOnly date, TimeOnly time) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, Jst);

    [Fact]
    public void BundledData_IsAvailableWithoutAnyDownloadedCache()
    {
        var service = new JapaneseHolidayService();

        Assert.True(service.HasHolidayData);
        Assert.Null(service.DataWarning);
        Assert.NotNull(service.Calendar);
        Assert.Equal(1067, service.GetHolidays().Count);
        Assert.Equal(new DateOnly(1955, 1, 1), service.Calendar!.CoverageStartInclusive);
        Assert.Equal(new DateOnly(2027, 11, 23), service.Calendar.CoverageEndInclusive);
    }

    [Fact]
    public void OrdinaryDay_Fires_AndNationalHoliday_IsSkipped()
    {
        var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService());

        // 2026-11-19 (Thursday) is an ordinary day.
        var ordinary = calculator.CalculateNextFire(
            AlarmAt(new TimeOnly(7, 0)), JstAt(new DateOnly(2026, 11, 19), new TimeOnly(0, 0)));
        Assert.NotNull(ordinary);
        Assert.Equal(new DateOnly(2026, 11, 19), ordinary!.LocalDate);

        // 2026-11-23 (勤労感謝の日, Monday) must be skipped to 2026-11-24.
        var holiday = calculator.CalculateNextFire(
            AlarmAt(new TimeOnly(7, 0)), JstAt(new DateOnly(2026, 11, 22), new TimeOnly(8, 0)));
        Assert.NotNull(holiday);
        Assert.Equal(new DateOnly(2026, 11, 24), holiday!.LocalDate);
        Assert.Equal(new TimeOnly(7, 0), holiday.LocalTime);
    }

    [Fact]
    public void SubstituteHoliday_IsSkipped()
    {
        var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService());

        // 2026-05-06 is the substitute holiday for Constitution Memorial Day on a Wednesday.
        var next = calculator.CalculateNextFire(
            AlarmAt(new TimeOnly(7, 0)), JstAt(new DateOnly(2026, 5, 5), new TimeOnly(8, 0)));

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 5, 7), next!.LocalDate);
    }

    [Fact]
    public void CitizenHoliday_IsSkipped()
    {
        var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService());

        // 2026-09-22 is a citizen's holiday between Respect for the Aged Day (21st) and the
        // Autumnal Equinox (23rd), so the whole run is skipped and the alarm lands on the 24th.
        var next = calculator.CalculateNextFire(
            AlarmAt(new TimeOnly(7, 0)), JstAt(new DateOnly(2026, 9, 20), new TimeOnly(8, 0)));

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 9, 24), next!.LocalDate);
    }

    [Fact]
    public void DatesOutsideBundledCoverage_AreUnknown_SoTheAlarmStillFires()
    {
        var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService());

        // 2027-11-24 is the first day after the bundled coverage end (2027-11-23).
        var afterCoverage = AlarmAt(new TimeOnly(7, 0));
        var next = calculator.CalculateNextFire(
            afterCoverage, JstAt(new DateOnly(2027, 11, 23), new TimeOnly(8, 0)));
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2027, 11, 24), next!.LocalDate);
        Assert.True(afterCoverage.HasHolidayCoverageWarning);

        // 2028-01-10 (成人の日) is outside coverage: unknown, not skipped.
        var nextYear = AlarmAt(new TimeOnly(7, 0));
        var later = calculator.CalculateNextFire(
            nextYear, JstAt(new DateOnly(2028, 1, 9), new TimeOnly(8, 0)));
        Assert.NotNull(later);
        Assert.Equal(new DateOnly(2028, 1, 10), later!.LocalDate);
        Assert.True(nextYear.HasHolidayCoverageWarning);
    }

    [Fact]
    public void AsiaTokyoMidnightBoundary_UsesTheLocalCalendarDate()
    {
        var calculator = new AlarmScheduleCalculator(new JapaneseHolidayService());

        // One minute before midnight on 2026-11-22 JST the next 00:05 alarm is on the holiday.
        var next = calculator.CalculateNextFire(
            AlarmAt(new TimeOnly(0, 5)), JstAt(new DateOnly(2026, 11, 22), new TimeOnly(23, 59)));

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 11, 24), next!.LocalDate);
        Assert.Equal(new TimeOnly(0, 5), next.LocalTime);
    }
}
