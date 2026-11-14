using System;
using System.Collections.Generic;
using WakeMesh.Core;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class HolidayServiceTests
{
    [Fact]
    public void InitializedJapaneseHolidayService_ProvidesKnownJapaneseHolidays()
    {
        var testHolidays = new List<DateOnly>
        {
            new(2026, 1, 1),
            new(2026, 1, 12),
            new(2026, 2, 11),
            new(2026, 2, 23)
        };
        var service = new JapaneseHolidayService(holidays: testHolidays);

        Assert.True(service.HasHolidayData);
        Assert.Null(service.DataWarning);

        var holidays = service.GetHolidays();
        Assert.NotEmpty(holidays);

        // 元日 2026-01-01 は祝日
        Assert.Contains(new DateOnly(2026, 1, 1), holidays);
        // 成人の日 2026-01-12 は祝日
        Assert.Contains(new DateOnly(2026, 1, 12), holidays);
        Assert.True(service.IsHoliday(new DateOnly(2026, 1, 1)));
        Assert.False(service.IsHoliday(new DateOnly(2026, 1, 2)));
    }

    [Fact]
    public void UninitializedHolidayService_WarnsAndReturnsEmptyHolidaysForPriorityFiring()
    {
        var service = new JapaneseHolidayService(holidays: null, warningMessage: "祝日データが未取得です。起床を優先しアラームは鳴動します。");

        Assert.False(service.HasHolidayData);
        Assert.NotNull(service.DataWarning);
        Assert.Contains("起床を優先", service.DataWarning);
        Assert.Empty(service.GetHolidays());
        // Fails open: returns false so alarm rings even on potential holiday
        Assert.False(service.IsHoliday(new DateOnly(2026, 1, 1)));
    }
}
