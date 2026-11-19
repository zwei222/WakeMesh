using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Core;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Holiday lookup for the app. The only data source is the JpCalendar data bundled into the app
/// through <see cref="BundledHolidayCalendar"/>; there is no runtime download or cache write.
/// Existing seams (constructor injection, <see cref="IHolidayService"/>) are preserved.
/// </summary>
public sealed class JapaneseHolidayService : IHolidayService
{
    private const string MissingDataWarning = "祝日情報が未取得です（祝日もアラーム対象）";

    private HolidayCalendar? _calendar;
    private string? _warningMessage;
    private readonly object _sync = new();

    public bool HasHolidayData => _calendar is not null;
    public string? DataWarning => _warningMessage;
    public HolidayCalendar? Calendar => _calendar;

    /// <summary>Bundled data is fixed at build time, so it never needs a refresh.</summary>
    public bool NeedsRefresh => false;

    public string HolidayStatusSummary
    {
        get
        {
            var calendar = _calendar;
            if (calendar is null)
            {
                return _warningMessage ?? MissingDataWarning;
            }

            var coverage = $"{calendar.CoverageStartInclusive:yyyy/MM/dd}〜{calendar.CoverageEndInclusive:yyyy/MM/dd}";
            if (!string.IsNullOrEmpty(_warningMessage))
            {
                return $"{_warningMessage} [内蔵祝日データ: {coverage}]";
            }

            var label = string.IsNullOrWhiteSpace(calendar.VersionLabel)
                ? "内蔵祝日データ"
                : calendar.VersionLabel;
            return $"祝日データ適用済み [{label}、{coverage}、※カバレッジ外は未判定・祝日もアラーム対象]";
        }
    }

    private JapaneseHolidayService(HolidayCalendar? calendar, string? warningMessage)
    {
        _calendar = calendar;
        _warningMessage = warningMessage;
    }

    /// <summary>Production entry point: the verified JpCalendar data compiled into the app.</summary>
    public static JapaneseHolidayService CreateBundled() => new(BundledHolidayCalendar.Current);

    /// <summary>Explicit "no holiday data" fixture: alarms fail open and ring on holidays.</summary>
    public static JapaneseHolidayService CreateWithoutData(string? warningMessage = null) =>
        new(null, warningMessage ?? MissingDataWarning);

    [ActivatorUtilitiesConstructor]
    public JapaneseHolidayService() : this(BundledHolidayCalendar.Current)
    {
    }

    private JapaneseHolidayService(BundledHolidayCalendarResult result)
        : this(result.Calendar, result.Calendar is null ? result.FailureReason : null)
    {
    }

    /// <summary>
    /// Injected-calendar seam used by tests and callers that already hold a calendar.
    /// A null <paramref name="initialCalendar"/> still means "no data" (fail open).
    /// <param name="cacheFilePath"/> is accepted for source compatibility only: legacy cache
    /// bytes are never read, written or migrated.
    /// </summary>
    public JapaneseHolidayService(
        HolidayCalendar? initialCalendar = null,
        string? cacheFilePath = null,
        string? warningMessage = null)
        : this(initialCalendar, initialCalendar is null ? warningMessage ?? MissingDataWarning : warningMessage)
    {
        _ = cacheFilePath;
    }

    public JapaneseHolidayService(
        IReadOnlyList<DateOnly>? holidays,
        string? warningMessage = null,
        string? cacheFilePath = null)
        : this(BuildFromDates(holidays), holidays is null || holidays.Count == 0 ? warningMessage ?? MissingDataWarning : warningMessage)
    {
        _ = cacheFilePath;
    }

    private static HolidayCalendar? BuildFromDates(IReadOnlyList<DateOnly>? holidays)
    {
        if (holidays is null || holidays.Count == 0)
        {
            return null;
        }

        var dates = holidays.OrderBy(d => d).ToList();
        return new HolidayCalendar
        {
            RetrievedAt = DateTimeOffset.UtcNow,
            VersionLabel = "テスト祝日データ",
            CoverageStartInclusive = dates[0],
            CoverageEndInclusive = dates[^1],
            Dates = dates.ToHashSet()
        };
    }

    public bool IsHoliday(DateOnly date)
    {
        var calendar = _calendar;
        if (calendar is null)
        {
            // Fail-open: without trusted holiday data, ring on schedule (alarm priority).
            return false;
        }

        return calendar.Classify(date) == HolidayDateKind.Holiday;
    }

    public IReadOnlySet<DateOnly> GetHolidays() =>
        _calendar?.Dates ?? (IReadOnlySet<DateOnly>)new HashSet<DateOnly>();

    /// <summary>
    /// The diagnostics refresh action re-checks the bundled data. It never performs a download or
    /// touches the legacy cache; when the data is available the caller recalculates and re-registers.
    /// </summary>
    public Task<bool> RefreshHolidaysAsync()
    {
        var result = BundledHolidayCalendar.Current;
        if (result.Calendar is null)
        {
            lock (_sync)
            {
                _warningMessage = result.FailureReason;
            }

            return Task.FromResult(false);
        }

        lock (_sync)
        {
            // Keep an explicitly injected calendar; only fill the "no data" state from the bundle.
            if (_calendar is null)
            {
                _calendar = result.Calendar;
                _warningMessage = null;
            }
        }

        return Task.FromResult(true);
    }
}
