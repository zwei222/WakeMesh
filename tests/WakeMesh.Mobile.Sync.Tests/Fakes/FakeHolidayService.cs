using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Sync.Tests.Fakes;

public sealed class FakeHolidayService : IHolidayService
{
    public bool HasHolidayData => true;
    public string? DataWarning => null;
    public HolidayCalendar? Calendar => null;
    public string HolidayStatusSummary => "Fake holiday calendar";
    public bool NeedsRefresh => false;

    public IReadOnlySet<DateOnly> Holidays { get; set; } = new HashSet<DateOnly>();

    public IReadOnlySet<DateOnly> GetHolidays() => Holidays;

    public Task<bool> RefreshHolidaysAsync() => Task.FromResult(true);
}
