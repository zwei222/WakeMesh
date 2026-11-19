using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WakeMesh.Core;

/// <summary>
/// Result of building the holiday calendar from the bundled JpCalendar data.
/// A null <see cref="Calendar"/> means the data could not be trusted; callers must fail open
/// (ring on holidays) and surface <see cref="FailureReason"/> instead of skipping alarms.
/// </summary>
public sealed record BundledHolidayCalendarResult(HolidayCalendar? Calendar, string? FailureReason)
{
    public bool HasData => Calendar is not null;
}

/// <summary>
/// Holiday data compiled into the app through the JpCalendar 2.0.0 package.
/// The calendar is materialised once per process and verified against a pinned digest so a broken
/// or substituted package cannot silently change holiday behaviour.
/// </summary>
public static class BundledHolidayCalendar
{
    public const string PackageVersion = "2.0.0";

    /// <summary>
    /// SHA-256 over the canonical date list: sorted invariant <c>yyyy-MM-dd</c> lines, each
    /// terminated by LF (including the final line), covering 1955-01-01..2027-11-23 (1067 dates).
    /// </summary>
    public const string ExpectedSha256 = "ce7ea9e01c54bc24f36e2539e656e14fc3ef8a24b73bcc3f59ceab702c0d2555";

    public const int ExpectedCount = 1067;

    public static readonly DateOnly CoverageStartInclusive = new(1955, 1, 1);
    public static readonly DateOnly CoverageEndInclusive = new(2027, 11, 23);

    /// <summary>
    /// Stable verification date for the bundled data. Deliberately not <see cref="DateTimeOffset.UtcNow"/>
    /// so scheduling and diagnostics are reproducible on every launch.
    /// </summary>
    public static readonly DateTimeOffset VerificationTimestamp =
        new(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(9));

    public const string VersionLabel = "内蔵祝日データ (JpCalendar 2.0.0)";

    private static readonly Lazy<BundledHolidayCalendarResult> LazyResult = new(
        () => Build(
            JpCalendar.Calendar.IsNationalHoliday,
            CoverageStartInclusive,
            CoverageEndInclusive,
            ExpectedSha256,
            ExpectedCount),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The bundled calendar, or a failure result that callers must treat as "no data".</summary>
    public static BundledHolidayCalendarResult Current => LazyResult.Value;

    public static bool TryGet(out HolidayCalendar? calendar)
    {
        calendar = LazyResult.Value.Calendar;
        return calendar is not null;
    }

    /// <summary>
    /// Pure, testable builder. Verifies count, first/last date and the canonical date-list digest.
    /// Any exception or mismatch yields a failure result, never a partial calendar.
    /// </summary>
    public static BundledHolidayCalendarResult Build(
        Func<DateOnly, bool> isNationalHoliday,
        DateOnly coverageStartInclusive,
        DateOnly coverageEndInclusive,
        string expectedSha256,
        int expectedCount)
    {
        ArgumentNullException.ThrowIfNull(isNationalHoliday);

        try
        {
            var dates = new List<DateOnly>(expectedCount);
            for (var date = coverageStartInclusive; date <= coverageEndInclusive; date = date.AddDays(1))
            {
                if (isNationalHoliday(date))
                {
                    dates.Add(date);
                }
            }

            dates.Sort();

            if (dates.Count != expectedCount)
            {
                return Fail($"日付数が想定と異なります ({dates.Count} != {expectedCount})");
            }

            if (dates[0] != coverageStartInclusive || dates[^1] != coverageEndInclusive)
            {
                return Fail(
                    $"カバレッジ端の日付が想定と異なります ({dates[0]:yyyy-MM-dd}/{dates[^1]:yyyy-MM-dd})");
            }

            var canonical = BuildCanonicalCsv(dates);
            var digest = ComputeSha256(canonical);
            if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
            {
                return Fail($"日付一覧の sha256 が一致しません ({digest})");
            }

            var calendar = new HolidayCalendar
            {
                RetrievedAt = VerificationTimestamp,
                VersionLabel = VersionLabel,
                CoverageStartInclusive = coverageStartInclusive,
                CoverageEndInclusive = coverageEndInclusive,
                Dates = dates.ToHashSet()
            };

            return new BundledHolidayCalendarResult(calendar, null);
        }
        catch (Exception ex)
        {
            return Fail($"例外: {ex.Message}");
        }
    }

    /// <summary>Canonical text form of a date list used for the pinned digest.</summary>
    public static string BuildCanonicalCsv(IEnumerable<DateOnly> dates)
    {
        ArgumentNullException.ThrowIfNull(dates);

        var sb = new StringBuilder();
        foreach (var date in dates.OrderBy(d => d))
        {
            sb.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    public static string ComputeSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static BundledHolidayCalendarResult Fail(string reason) =>
        new(null, $"内蔵祝日データを確認できませんでした（{reason}）。祝日もアラーム対象です。");
}
