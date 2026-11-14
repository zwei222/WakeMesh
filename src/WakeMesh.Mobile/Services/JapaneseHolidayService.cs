using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Core;

namespace WakeMesh.Mobile.Services;

public sealed class JapaneseHolidayService : IHolidayService
{
    public const string CabinetOfficeHolidayCsvUrl = "https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv";

    private HolidayCalendar? _calendar;
    private string? _warningMessage;
    private readonly string _cacheFilePath;
    private readonly HolidayCalendarParser _parser = new();
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public bool HasHolidayData => _calendar is not null;
    public string? DataWarning => _warningMessage;
    public HolidayCalendar? Calendar => _calendar;

    public bool NeedsRefresh
    {
        get
        {
            if (_calendar is null) return true;
            return (DateTimeOffset.UtcNow - _calendar.RetrievedAt) > TimeSpan.FromDays(30);
        }
    }

    public string HolidayStatusSummary
    {
        get
        {
            if (_calendar is null)
            {
                return _warningMessage ?? "祝日情報が未取得です（祝日もアラーム対象）";
            }

            var coverage = $"{_calendar.CoverageStartInclusive:yyyy/MM/dd}〜{_calendar.CoverageEndInclusive:yyyy/MM/dd}";
            if (!string.IsNullOrEmpty(_warningMessage))
            {
                return $"{_warningMessage} [現行データ: {coverage}]";
            }
            return $"祝日データ適用済み ({coverage}、※最終祝日以降の年末はCore仕様上未判定・祝日もアラーム対象)";
        }
    }

    public JapaneseHolidayService()
        : this(initialCalendar: null, cacheFilePath: null, warningMessage: null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public JapaneseHolidayService(
        HolidayCalendar? initialCalendar = null,
        string? cacheFilePath = null,
        string? warningMessage = null)
    {
        _cacheFilePath = cacheFilePath ?? AlarmStoragePathHelper.GetHolidayCacheFilePath();
        _warningMessage = warningMessage;

        if (initialCalendar is not null)
        {
            _calendar = initialCalendar;
        }
        else if (File.Exists(_cacheFilePath))
        {
            try
            {
                using var fs = File.OpenRead(_cacheFilePath);
                var lastWrite = File.GetLastWriteTimeUtc(_cacheFilePath);
                _calendar = _parser.Parse(fs, lastWrite, "キャッシュ祝日データ");
            }
            catch (Exception ex)
            {
                _warningMessage = $"キャッシュ読み込み失敗: {ex.Message}";
                _calendar = null;
            }
        }
        else
        {
            _calendar = null;
            _warningMessage ??= "祝日情報が未取得です（祝日もアラーム対象）";
        }
    }

    public JapaneseHolidayService(
        IReadOnlyList<DateOnly>? holidays,
        string? warningMessage = null,
        string? cacheFilePath = null)
    {
        _cacheFilePath = cacheFilePath ?? AlarmStoragePathHelper.GetHolidayCacheFilePath();
        _warningMessage = warningMessage;

        if (holidays is not null)
        {
            var datesList = holidays.OrderBy(d => d).ToList();
            if (datesList.Count > 0)
            {
                _calendar = new HolidayCalendar
                {
                    RetrievedAt = DateTimeOffset.UtcNow,
                    VersionLabel = "テスト祝日データ",
                    CoverageStartInclusive = datesList[0],
                    CoverageEndInclusive = datesList[^1],
                    Dates = datesList.ToHashSet()
                };
            }
        }
        else
        {
            _calendar = null;
            _warningMessage ??= "祝日情報が未取得です（祝日もアラーム対象）";
        }
    }

    public bool IsHoliday(DateOnly date)
    {
        if (_calendar is null)
        {
            // Fail-open: Without holiday data, ring on schedule (alarm priority)
            return false;
        }

        var classification = _calendar.Classify(date);
        return classification == HolidayDateKind.Holiday;
    }

    public IReadOnlySet<DateOnly> GetHolidays()
    {
        return _calendar?.Dates ?? (IReadOnlySet<DateOnly>)new HashSet<DateOnly>();
    }

    public Task<bool> RefreshHolidaysAsync() => RefreshFromOfficialAsync();

    public async Task<bool> RefreshFromOfficialAsync(HttpClient? httpClient = null)
    {
        var client = httpClient ?? DefaultHttpClient;
        try
        {
            using var response = await client.GetAsync(CabinetOfficeHolidayCsvUrl);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var stream = new MemoryStream(bytes);

            var calendar = _parser.Parse(stream, DateTimeOffset.UtcNow, "内閣府公式祝日CSV");

            // Save to cache file atomically
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempFile = _cacheFilePath + ".tmp." + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(tempFile, bytes);
            File.Move(tempFile, _cacheFilePath, overwrite: true);

            _calendar = calendar;
            _warningMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            // Maintain existing data on failure, do not wipe
            _warningMessage = _calendar is not null
                ? $"祝日データ更新失敗 (既存データ維持): {ex.Message}"
                : $"祝日データ取得失敗（祝日もアラーム対象）: {ex.Message}";
            return false;
        }
    }
}
