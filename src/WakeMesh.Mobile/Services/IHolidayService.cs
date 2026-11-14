using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Core;

namespace WakeMesh.Mobile.Services;

public interface IHolidayService
{
    bool HasHolidayData { get; }
    string? DataWarning { get; }
    HolidayCalendar? Calendar { get; }
    string HolidayStatusSummary { get; }
    bool NeedsRefresh { get; }
    IReadOnlySet<DateOnly> GetHolidays();
    Task<bool> RefreshHolidaysAsync();
}
