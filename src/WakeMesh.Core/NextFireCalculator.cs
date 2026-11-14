namespace WakeMesh.Core;

public enum AlarmRepeatKind
{
    Once = 0,
    Weekly = 1
}

[Flags]
public enum Weekdays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6
}

public sealed record DeviceLocalTime(string DeviceId, TimeOnly LocalTime);

public sealed class AlarmDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsEnabled { get; init; }
    public required AlarmRepeatKind RepeatKind { get; init; }
    public DateOnly? OnceOnDate { get; init; }
    public Weekdays ActiveWeekdays { get; init; }
    public required string TimeZoneId { get; init; }
    public required IReadOnlyList<DeviceLocalTime> DeviceTimes { get; init; }
    public bool ExcludeHolidays { get; init; }
    public IReadOnlySet<DateOnly> ExcludedDates { get; init; } = new HashSet<DateOnly>();
}

public sealed record NextFire(
    DateTimeOffset Instant,
    string DeviceId,
    DateOnly LocalDate,
    TimeOnly LocalTime);

public sealed class NextFireCalculator
{
    private static readonly TimeSpan DefaultSearchWindow = TimeSpan.FromDays(366 * 2);

    public NextFire? GetNextFire(
        AlarmDefinition alarm,
        string deviceId,
        DateTimeOffset afterExclusive,
        IReadOnlySet<DateOnly>? holidays = null,
        DateTimeOffset? searchUntilExclusive = null)
    {
        return GetNextFireCore(
            alarm,
            deviceId,
            afterExclusive,
            HolidayPolicy.FromDateSet(holidays),
            searchUntilExclusive,
            out _);
    }

    /// <summary>
    /// Computes the next fire using a coverage-aware holiday calendar.
    /// When <paramref name="holidayCoverageWarning"/> is true, at least one candidate day was
    /// outside the calendar coverage while holiday exclusion was requested; the calculator still
    /// prefers firing (does not treat unknown days as holidays).
    /// </summary>
    public NextFire? GetNextFire(
        AlarmDefinition alarm,
        string deviceId,
        DateTimeOffset afterExclusive,
        HolidayCalendar holidayCalendar,
        out bool holidayCoverageWarning,
        DateTimeOffset? searchUntilExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(holidayCalendar);
        return GetNextFireCore(
            alarm,
            deviceId,
            afterExclusive,
            HolidayPolicy.FromCalendar(holidayCalendar),
            searchUntilExclusive,
            out holidayCoverageWarning);
    }

    private static NextFire? GetNextFireCore(
        AlarmDefinition alarm,
        string deviceId,
        DateTimeOffset afterExclusive,
        HolidayPolicy holidayPolicy,
        DateTimeOffset? searchUntilExclusive,
        out bool holidayCoverageWarning)
    {
        holidayCoverageWarning = false;
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!alarm.IsEnabled)
        {
            return null;
        }

        ValidateRepeatConfiguration(alarm);

        if (string.IsNullOrWhiteSpace(alarm.TimeZoneId))
        {
            throw new ArgumentException("TimeZoneId is required.", nameof(alarm));
        }

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(alarm.TimeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException($"Unknown time zone '{alarm.TimeZoneId}'.", nameof(alarm), ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new ArgumentException($"Invalid time zone '{alarm.TimeZoneId}'.", nameof(alarm), ex);
        }

        var deviceTime = alarm.DeviceTimes.FirstOrDefault(d => d.DeviceId == deviceId)
            ?? throw new ArgumentException($"Device '{deviceId}' is not configured on this alarm.", nameof(deviceId));

        var searchUntil = searchUntilExclusive ?? SafeAdd(afterExclusive, DefaultSearchWindow);
        if (searchUntil <= afterExclusive)
        {
            return null;
        }

        var warning = false;
        NextFire? result = alarm.RepeatKind switch
        {
            AlarmRepeatKind.Weekly => FindNextWeekly(
                alarm, deviceId, deviceTime.LocalTime, afterExclusive, searchUntil, timeZone, holidayPolicy, ref warning),
            AlarmRepeatKind.Once => FindNextOnce(
                alarm, deviceId, deviceTime.LocalTime, afterExclusive, searchUntil, timeZone, holidayPolicy, ref warning),
            _ => throw new ArgumentException($"Unsupported repeat kind '{alarm.RepeatKind}'.", nameof(alarm))
        };
        holidayCoverageWarning = warning;
        return result;
    }

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan delta)
    {
        try
        {
            return value + delta;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private readonly struct HolidayPolicy
    {
        private readonly IReadOnlySet<DateOnly>? _dates;
        private readonly HolidayCalendar? _calendar;

        private HolidayPolicy(IReadOnlySet<DateOnly>? dates, HolidayCalendar? calendar)
        {
            _dates = dates;
            _calendar = calendar;
        }

        public static HolidayPolicy FromDateSet(IReadOnlySet<DateOnly>? dates) =>
            new(dates ?? new HashSet<DateOnly>(), calendar: null);

        public static HolidayPolicy FromCalendar(HolidayCalendar calendar) =>
            new(dates: null, calendar);

        public bool IsExcludedHoliday(DateOnly date, bool excludeHolidays, ref bool coverageWarning)
        {
            if (!excludeHolidays)
            {
                return false;
            }

            if (_calendar is not null)
            {
                return _calendar.Classify(date) switch
                {
                    HolidayDateKind.Holiday => true,
                    HolidayDateKind.NonHoliday => false,
                    HolidayDateKind.OutsideCoverage => ObserveOutside(ref coverageWarning),
                    _ => false
                };
            }

            return _dates!.Contains(date);
        }

        private static bool ObserveOutside(ref bool coverageWarning)
        {
            coverageWarning = true;
            // Prefer firing when coverage is unknown — not treated as a holiday.
            return false;
        }
    }

    private static readonly Weekdays ValidWeekdayMask =
        Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday |
        Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday;

    private static void ValidateRepeatConfiguration(AlarmDefinition alarm)
    {
        switch (alarm.RepeatKind)
        {
            case AlarmRepeatKind.Weekly when alarm.ActiveWeekdays == Weekdays.None:
                throw new ArgumentException("Weekly alarms require at least one active weekday.", nameof(alarm));
            case AlarmRepeatKind.Weekly when (alarm.ActiveWeekdays & ~ValidWeekdayMask) != 0:
                throw new ArgumentException("ActiveWeekdays contains undefined weekday bits.", nameof(alarm));
            case AlarmRepeatKind.Once when alarm.OnceOnDate is null:
                throw new ArgumentException("One-shot alarms require OnceOnDate.", nameof(alarm));
            case AlarmRepeatKind.Weekly:
            case AlarmRepeatKind.Once:
                break;
            default:
                throw new ArgumentException($"Unsupported repeat kind '{alarm.RepeatKind}'.", nameof(alarm));
        }

        if (alarm.DeviceTimes is null || alarm.DeviceTimes.Count == 0)
        {
            throw new ArgumentException("At least one device local time is required.", nameof(alarm));
        }

        for (var i = 0; i < alarm.DeviceTimes.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(alarm.DeviceTimes[i].DeviceId))
            {
                throw new ArgumentException("DeviceTimes entries must not use blank device ids.", nameof(alarm));
            }
        }

        if (alarm.DeviceTimes.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count() != alarm.DeviceTimes.Count)
        {
            throw new ArgumentException("DeviceTimes must use unique device ids.", nameof(alarm));
        }
    }

    private static NextFire? FindNextWeekly(
        AlarmDefinition alarm,
        string deviceId,
        TimeOnly localTime,
        DateTimeOffset afterExclusive,
        DateTimeOffset searchUntilExclusive,
        TimeZoneInfo timeZone,
        HolidayPolicy holidayPolicy,
        ref bool coverageWarning)
    {
        var localAfter = TimeZoneInfo.ConvertTime(afterExclusive, timeZone);
        var startDate = DateOnly.FromDateTime(localAfter.DateTime);
        var localUntil = TimeZoneInfo.ConvertTime(searchUntilExclusive, timeZone);
        var lastDate = DateOnly.FromDateTime(localUntil.DateTime);
        if (lastDate < DateOnly.MaxValue)
        {
            // Allow one extra local day so late-evening instants near the boundary remain visible.
            lastDate = lastDate.AddDays(1);
        }

        for (var date = startDate; ;)
        {
            if (MatchesWeekday(date.DayOfWeek, alarm.ActiveWeekdays))
            {
                var candidate = ResolveLocalInstant(date, localTime, timeZone);
                if (candidate is not null)
                {
                    var effectiveDate = candidate.Value.EffectiveLocalDate;
                    if (!IsExcluded(effectiveDate, alarm, holidayPolicy, ref coverageWarning)
                        && candidate.Value.Instant > afterExclusive)
                    {
                        if (candidate.Value.Instant >= searchUntilExclusive)
                        {
                            return null;
                        }

                        return new NextFire(
                            candidate.Value.Instant,
                            deviceId,
                            effectiveDate,
                            candidate.Value.EffectiveLocalTime);
                    }
                }
            }

            if (date >= lastDate || date >= DateOnly.MaxValue)
            {
                return null;
            }

            date = date.AddDays(1);
        }
    }

    private static NextFire? FindNextOnce(
        AlarmDefinition alarm,
        string deviceId,
        TimeOnly localTime,
        DateTimeOffset afterExclusive,
        DateTimeOffset searchUntilExclusive,
        TimeZoneInfo timeZone,
        HolidayPolicy holidayPolicy,
        ref bool coverageWarning)
    {
        var date = alarm.OnceOnDate!.Value;
        var candidate = ResolveLocalInstant(date, localTime, timeZone);
        if (candidate is null)
        {
            return null;
        }

        var effectiveDate = candidate.Value.EffectiveLocalDate;
        if (IsExcluded(effectiveDate, alarm, holidayPolicy, ref coverageWarning))
        {
            return null;
        }

        if (candidate.Value.Instant <= afterExclusive)
        {
            return null;
        }

        if (candidate.Value.Instant >= searchUntilExclusive)
        {
            return null;
        }

        return new NextFire(
            candidate.Value.Instant,
            deviceId,
            effectiveDate,
            candidate.Value.EffectiveLocalTime);
    }

    private static bool IsExcluded(
        DateOnly date,
        AlarmDefinition alarm,
        HolidayPolicy holidayPolicy,
        ref bool coverageWarning)
    {
        if (alarm.ExcludedDates.Contains(date))
        {
            return true;
        }

        return holidayPolicy.IsExcludedHoliday(date, alarm.ExcludeHolidays, ref coverageWarning);
    }

    private static bool MatchesWeekday(DayOfWeek dayOfWeek, Weekdays active)
    {
        var flag = dayOfWeek switch
        {
            DayOfWeek.Monday => Weekdays.Monday,
            DayOfWeek.Tuesday => Weekdays.Tuesday,
            DayOfWeek.Wednesday => Weekdays.Wednesday,
            DayOfWeek.Thursday => Weekdays.Thursday,
            DayOfWeek.Friday => Weekdays.Friday,
            DayOfWeek.Saturday => Weekdays.Saturday,
            DayOfWeek.Sunday => Weekdays.Sunday,
            _ => Weekdays.None
        };

        return (active & flag) != 0;
    }

    /// <summary>
    /// DST gap (spring forward): first valid local time after the missing range (second precision).
    /// DST overlap (fall back): first (earlier) occurrence.
    /// </summary>
    private static (DateTimeOffset Instant, DateOnly EffectiveLocalDate, TimeOnly EffectiveLocalTime)? ResolveLocalInstant(
        DateOnly date,
        TimeOnly localTime,
        TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(localTime), DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(unspecified))
        {
            // Entire missing local days (e.g. Pacific/Apia 2011-12-30) may span >24h of invalid wall times.
            var cursor = unspecified;
            const int maxSeconds = 48 * 60 * 60;
            for (var i = 0; i < maxSeconds; i++)
            {
                cursor = cursor.AddSeconds(1);
                if (!timeZone.IsInvalidTime(cursor))
                {
                    var adjustedOffset = timeZone.GetUtcOffset(cursor);
                    var instant = new DateTimeOffset(cursor, adjustedOffset);
                    return (
                        instant,
                        DateOnly.FromDateTime(cursor),
                        TimeOnly.FromDateTime(cursor));
                }
            }

            return null;
        }

        if (timeZone.IsAmbiguousTime(unspecified))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(unspecified);
            var preferredOffset = offsets.OrderByDescending(o => o).First();
            var instant = new DateTimeOffset(unspecified, preferredOffset);
            return (instant, date, localTime);
        }

        {
            var offset = timeZone.GetUtcOffset(unspecified);
            var instant = new DateTimeOffset(unspecified, offset);
            return (instant, date, localTime);
        }
    }
}
