using WakeMesh.Core;

namespace WakeMesh.Core.Tests;

public sealed class NextFireCalculatorTests
{
    private static AlarmDefinition BaseWeekly(
        bool enabled = true,
        Weekdays days = Weekdays.Monday,
        string timeZoneId = "Asia/Tokyo",
        TimeOnly? deviceTime = null,
        bool excludeHolidays = false,
        IReadOnlySet<DateOnly>? excludedDates = null,
        IReadOnlyList<DeviceLocalTime>? deviceTimes = null)
    {
        return new AlarmDefinition
        {
            Id = "alarm-sample-1",
            Name = "Sample weekday alarm",
            IsEnabled = enabled,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = days,
            TimeZoneId = timeZoneId,
            DeviceTimes = deviceTimes ??
            [
                new DeviceLocalTime("device-alpha", deviceTime ?? new TimeOnly(7, 0))
            ],
            ExcludeHolidays = excludeHolidays,
            ExcludedDates = excludedDates ?? new HashSet<DateOnly>()
        };
    }

    private static AlarmDefinition BaseOnce(
        DateOnly onceOnDate,
        bool enabled = true,
        string timeZoneId = "Asia/Tokyo",
        TimeOnly? deviceTime = null,
        bool excludeHolidays = false,
        IReadOnlySet<DateOnly>? excludedDates = null)
    {
        return new AlarmDefinition
        {
            Id = "alarm-once-1",
            Name = "Sample one-shot alarm",
            IsEnabled = enabled,
            RepeatKind = AlarmRepeatKind.Once,
            OnceOnDate = onceOnDate,
            TimeZoneId = timeZoneId,
            DeviceTimes =
            [
                new DeviceLocalTime("device-alpha", deviceTime ?? new TimeOnly(7, 0))
            ],
            ExcludeHolidays = excludeHolidays,
            ExcludedDates = excludedDates ?? new HashSet<DateOnly>()
        };
    }

    [Fact]
    public void GetNextFire_returns_null_when_alarm_is_disabled()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(enabled: false);
        var after = new DateTimeOffset(2024, 1, 8, 6, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.Null(next);
    }

    [Fact]
    public void GetNextFire_weekly_picks_next_matching_weekday_after_instant()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(days: Weekdays.Monday, deviceTime: new TimeOnly(7, 0));
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2024, 1, 8, 7, 0, 0, TimeSpan.FromHours(9)), next.Instant);
        Assert.Equal("device-alpha", next.DeviceId);
        Assert.Equal(new DateOnly(2024, 1, 8), next.LocalDate);
        Assert.Equal(new TimeOnly(7, 0), next.LocalTime);
    }

    [Fact]
    public void GetNextFire_once_returns_scheduled_instant_when_still_in_future()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseOnce(new DateOnly(2024, 2, 10), deviceTime: new TimeOnly(6, 30));
        var after = new DateTimeOffset(2024, 2, 9, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateTimeOffset(2024, 2, 10, 6, 30, 0, TimeSpan.FromHours(9)), next.Instant);
        Assert.Equal(new DateOnly(2024, 2, 10), next.LocalDate);
    }

    [Fact]
    public void GetNextFire_once_returns_null_when_scheduled_instant_already_passed()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseOnce(new DateOnly(2024, 2, 10), deviceTime: new TimeOnly(6, 30));
        var after = new DateTimeOffset(2024, 2, 10, 6, 30, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.Null(next);
    }

    [Fact]
    public void GetNextFire_skips_holiday_when_exclude_holidays_enabled()
    {
        var calculator = new NextFireCalculator();
        // 2024-01-08 Monday is treated as a holiday input set member.
        var holidays = new HashSet<DateOnly> { new(2024, 1, 8) };
        var alarm = BaseWeekly(days: Weekdays.Monday, excludeHolidays: true);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays);

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 1, 15), next.LocalDate);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 7, 0, 0, TimeSpan.FromHours(9)), next.Instant);
    }

    [Fact]
    public void GetNextFire_skips_explicit_excluded_dates()
    {
        var calculator = new NextFireCalculator();
        var excluded = new HashSet<DateOnly> { new(2024, 1, 8) };
        var alarm = BaseWeekly(days: Weekdays.Monday, excludedDates: excluded);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 1, 15), next.LocalDate);
    }

    [Fact]
    public void GetNextFire_uses_device_specific_local_times()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(
            days: Weekdays.Monday,
            deviceTimes:
            [
                new DeviceLocalTime("device-alpha", new TimeOnly(7, 0)),
                new DeviceLocalTime("device-beta", new TimeOnly(8, 15))
            ]);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var alpha = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());
        var beta = calculator.GetNextFire(alarm, "device-beta", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.Equal(new TimeOnly(7, 0), alpha.LocalTime);
        Assert.Equal(new TimeOnly(8, 15), beta.LocalTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 8, 7, 0, 0, TimeSpan.FromHours(9)), alpha.Instant);
        Assert.Equal(new DateTimeOffset(2024, 1, 8, 8, 15, 0, TimeSpan.FromHours(9)), beta.Instant);
    }

    [Fact]
    public void GetNextFire_interprets_wall_time_in_alarm_time_zone()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(
            days: Weekdays.Monday,
            timeZoneId: "America/New_York",
            deviceTime: new TimeOnly(7, 0));
        // Sunday noon UTC → Monday 07:00 America/New_York (EST = UTC-5 in January)
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.Zero);

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 1, 8), next.LocalDate);
        Assert.Equal(new TimeOnly(7, 0), next.LocalTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 8, 7, 0, 0, TimeSpan.FromHours(-5)), next.Instant);
    }

    [Fact]
    public void GetNextFire_dst_gap_uses_next_valid_local_time()
    {
        var calculator = new NextFireCalculator();
        // US spring forward 2024-03-10: 02:00-02:59 does not exist in America/New_York.
        var alarm = BaseOnce(
            onceOnDate: new DateOnly(2024, 3, 10),
            timeZoneId: "America/New_York",
            deviceTime: new TimeOnly(2, 30));
        var after = new DateTimeOffset(2024, 3, 9, 12, 0, 0, TimeSpan.FromHours(-5));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 3, 10), next.LocalDate);
        Assert.Equal(new TimeOnly(3, 0), next.LocalTime);
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 3, 0, 0, TimeSpan.FromHours(-4)), next.Instant);
    }

    [Fact]
    public void GetNextFire_dst_overlap_uses_first_occurrence()
    {
        var calculator = new NextFireCalculator();
        // US fall back 2024-11-03: 01:30 occurs twice. First = EDT (UTC-4).
        var alarm = BaseOnce(
            onceOnDate: new DateOnly(2024, 11, 3),
            timeZoneId: "America/New_York",
            deviceTime: new TimeOnly(1, 30));
        var after = new DateTimeOffset(2024, 11, 2, 12, 0, 0, TimeSpan.FromHours(-4));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new TimeOnly(1, 30), next.LocalTime);
        Assert.Equal(new DateTimeOffset(2024, 11, 3, 1, 30, 0, TimeSpan.FromHours(-4)), next.Instant);
    }

    [Fact]
    public void GetNextFire_throws_when_weekly_has_no_active_weekdays()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(days: Weekdays.None);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
    }

    [Fact]
    public void GetNextFire_throws_when_once_missing_date()
    {
        var calculator = new NextFireCalculator();
        var alarm = new AlarmDefinition
        {
            Id = "bad-once",
            Name = "bad",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Once,
            OnceOnDate = null,
            TimeZoneId = "Asia/Tokyo",
            DeviceTimes = [new DeviceLocalTime("device-alpha", new TimeOnly(7, 0))]
        };
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
    }

    [Fact]
    public void GetNextFire_throws_when_device_is_not_configured()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly();
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-unknown", after, holidays: new HashSet<DateOnly>()));
    }

    [Fact]
    public void GetNextFire_returns_null_when_search_window_ends_before_candidate()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(days: Weekdays.Monday, deviceTime: new TimeOnly(7, 0));
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));
        // Search ends before Monday 07:00.
        var until = new DateTimeOffset(2024, 1, 8, 6, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            holidays: new HashSet<DateOnly>(),
            searchUntilExclusive: until);

        Assert.Null(next);
    }

    [Fact]
    public void GetNextFire_throws_when_device_times_is_null()
    {
        var calculator = new NextFireCalculator();
        var alarm = new AlarmDefinition
        {
            Id = "alarm-null-devices",
            Name = "null devices",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday,
            TimeZoneId = "Asia/Tokyo",
            DeviceTimes = null!
        };
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var ex = Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
        Assert.Contains("device", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetNextFire_throws_when_device_times_is_empty()
    {
        var calculator = new NextFireCalculator();
        var alarm = new AlarmDefinition
        {
            Id = "alarm-empty-devices",
            Name = "empty devices",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday,
            TimeZoneId = "Asia/Tokyo",
            DeviceTimes = Array.Empty<DeviceLocalTime>()
        };
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var ex = Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
        Assert.Contains("device", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetNextFire_throws_when_device_id_is_blank()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(deviceTimes:
        [
            new DeviceLocalTime("  ", new TimeOnly(7, 0))
        ]);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var ex = Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetNextFire_throws_when_device_times_have_duplicate_ids()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(deviceTimes:
        [
            new DeviceLocalTime("device-alpha", new TimeOnly(7, 0)),
            new DeviceLocalTime("device-alpha", new TimeOnly(8, 0))
        ]);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var ex = Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
        Assert.Contains("unique", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetNextFire_throws_when_active_weekdays_has_undefined_bits()
    {
        var calculator = new NextFireCalculator();
        // Monday plus an undefined high bit.
        var alarm = BaseWeekly(days: Weekdays.Monday | (Weekdays)0x80);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var ex = Assert.Throws<ArgumentException>(() =>
            calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>()));
        Assert.Contains("weekday", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetNextFire_dst_gap_with_seconds_snaps_to_first_valid_local_time()
    {
        var calculator = new NextFireCalculator();
        // US spring forward 2024-03-10: 02:00-02:59:59 does not exist.
        var alarm = BaseOnce(
            onceOnDate: new DateOnly(2024, 3, 10),
            timeZoneId: "America/New_York",
            deviceTime: new TimeOnly(2, 30, 45));
        var after = new DateTimeOffset(2024, 3, 9, 12, 0, 0, TimeSpan.FromHours(-5));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 3, 10), next.LocalDate);
        Assert.Equal(new TimeOnly(3, 0, 0), next.LocalTime);
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 3, 0, 0, TimeSpan.FromHours(-4)), next.Instant);
    }

    [Fact]
    public void GetNextFire_search_window_beyond_fixed_day_cap_still_finds_candidate()
    {
        var calculator = new NextFireCalculator();
        // Only the first Monday after a long gap of excluded Mondays.
        var after = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(9));
        var target = new DateOnly(2026, 6, 1); // Monday, >800 days from after? 2024-01-01 to 2026-06-01 = 882 days
        Assert.Equal(DayOfWeek.Monday, target.DayOfWeek);
        Assert.True(target.DayNumber - DateOnly.FromDateTime(after.DateTime).DayNumber > 800);

        var alarm = BaseWeekly(days: Weekdays.Monday, deviceTime: new TimeOnly(7, 0));
        // Exclude every Monday until the Monday before target.
        var excluded = new HashSet<DateOnly>();
        for (var d = new DateOnly(2024, 1, 1); d < target; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Monday)
            {
                excluded.Add(d);
            }
        }

        alarm = BaseWeekly(
            days: Weekdays.Monday,
            deviceTime: new TimeOnly(7, 0),
            excludedDates: excluded);

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            holidays: new HashSet<DateOnly>(),
            searchUntilExclusive: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.FromHours(9)));

        Assert.NotNull(next);
        Assert.Equal(target, next.LocalDate);
    }

    [Fact]
    public void GetNextFire_near_dateonly_max_does_not_throw_on_day_overflow()
    {
        var calculator = new NextFireCalculator();
        var alarm = BaseWeekly(days: Weekdays.Monday, deviceTime: new TimeOnly(7, 0));
        // Local after near DateOnly.MaxValue so naive AddDays loops would overflow.
        var after = new DateTimeOffset(DateOnly.MaxValue.Year, 12, 20, 0, 0, 0, TimeSpan.FromHours(9));
        var until = new DateTimeOffset(DateOnly.MaxValue.Year, 12, 31, 0, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            holidays: new HashSet<DateOnly>(),
            searchUntilExclusive: until);

        // May be null if no Monday remains before DateOnly max, but must not throw.
        _ = next;
    }

    [Fact]
    public void GetNextFire_calendar_day_jump_uses_effective_local_date_for_exclusion()
    {
        var calculator = new NextFireCalculator();
        Assert.True(TryFindTimeZone("Pacific/Apia", out _), "Pacific/Apia timezone required for day-jump coverage");

        // 2011-12-30 did not exist in Pacific/Apia (IDL skip to 2011-12-31).
        // Landing day 2011-12-31 is excluded — must not fire under the scheduled OnceOnDate.
        var alarm = new AlarmDefinition
        {
            Id = "apia-jump",
            Name = "apia",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Once,
            OnceOnDate = new DateOnly(2011, 12, 30),
            TimeZoneId = "Pacific/Apia",
            DeviceTimes = [new DeviceLocalTime("device-alpha", new TimeOnly(12, 0))],
            ExcludedDates = new HashSet<DateOnly> { new(2011, 12, 31) }
        };
        var after = new DateTimeOffset(2011, 12, 28, 0, 0, 0, TimeSpan.FromHours(-10));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.Null(next);
    }

    [Fact]
    public void GetNextFire_calendar_day_jump_reports_effective_local_date()
    {
        var calculator = new NextFireCalculator();
        Assert.True(TryFindTimeZone("Pacific/Apia", out _), "Pacific/Apia timezone required for day-jump coverage");

        var alarm = new AlarmDefinition
        {
            Id = "apia-jump-date",
            Name = "apia",
            IsEnabled = true,
            RepeatKind = AlarmRepeatKind.Once,
            OnceOnDate = new DateOnly(2011, 12, 30),
            TimeZoneId = "Pacific/Apia",
            DeviceTimes = [new DeviceLocalTime("device-alpha", new TimeOnly(12, 0))],
            ExcludedDates = new HashSet<DateOnly>()
        };
        var after = new DateTimeOffset(2011, 12, 28, 0, 0, 0, TimeSpan.FromHours(-10));

        var next = calculator.GetNextFire(alarm, "device-alpha", after, holidays: new HashSet<DateOnly>());

        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2011, 12, 31), next.LocalDate);
        Assert.Equal(new TimeOnly(0, 0, 0), next.LocalTime);
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo? tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tz = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            tz = null;
            return false;
        }
    }
}

public sealed class NextFireCalculatorHolidayCalendarTests
{
    private static AlarmDefinition MondayAlarm(bool excludeHolidays) => new()
    {
        Id = "alarm-holcal",
        Name = "holiday calendar",
        IsEnabled = true,
        RepeatKind = AlarmRepeatKind.Weekly,
        ActiveWeekdays = Weekdays.Monday,
        TimeZoneId = "Asia/Tokyo",
        DeviceTimes = [new DeviceLocalTime("device-alpha", new TimeOnly(7, 0))],
        ExcludeHolidays = excludeHolidays,
        ExcludedDates = new HashSet<DateOnly>()
    };

    private static HolidayCalendar FixtureCalendar()
    {
        return new HolidayCalendar
        {
            RetrievedAt = new DateTimeOffset(2026, 11, 14, 3, 0, 0, TimeSpan.FromHours(9)),
            VersionLabel = "fixture-v1",
            CoverageStartInclusive = new DateOnly(2024, 1, 1),
            CoverageEndInclusive = new DateOnly(2024, 12, 31),
            Dates = new HashSet<DateOnly> { new(2024, 1, 8) } // Monday
        };
    }

    [Fact]
    public void GetNextFire_with_calendar_skips_covered_holiday()
    {
        var calculator = new NextFireCalculator();
        var alarm = MondayAlarm(excludeHolidays: true);
        var after = new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            FixtureCalendar(),
            out var warning);

        Assert.False(warning);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2024, 1, 15), next.LocalDate);
    }

    [Fact]
    public void GetNextFire_with_calendar_outside_coverage_fires_and_warns()
    {
        var calculator = new NextFireCalculator();
        var alarm = MondayAlarm(excludeHolidays: true);
        // 2025-01-06 is Monday outside 2024 coverage.
        var after = new DateTimeOffset(2025, 1, 5, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            FixtureCalendar(),
            out var warning);

        Assert.True(warning);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2025, 1, 6), next.LocalDate);
    }

    [Fact]
    public void GetNextFire_with_calendar_no_warning_when_exclude_holidays_disabled()
    {
        var calculator = new NextFireCalculator();
        var alarm = MondayAlarm(excludeHolidays: false);
        var after = new DateTimeOffset(2025, 1, 5, 12, 0, 0, TimeSpan.FromHours(9));

        var next = calculator.GetNextFire(
            alarm,
            "device-alpha",
            after,
            FixtureCalendar(),
            out var warning);

        Assert.False(warning);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2025, 1, 6), next.LocalDate);
    }
}
