using System;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

public sealed class T23DaysSummaryTests
{
    [Theory]
    [InlineData(0, "日")]
    [InlineData(1, "月")]
    [InlineData(2, "火")]
    [InlineData(3, "水")]
    [InlineData(4, "木")]
    [InlineData(5, "金")]
    [InlineData(6, "土")]
    public void WireDaySummary_UsesSundayZeroThroughSaturdaySix(int day, string expected)
    {
        Assert.Equal(expected, Create(day).DaysSummary);
    }

    [Fact]
    public void WeekdaysAndUnknownWireDay_AreHonest()
    {
        Assert.Equal("平日 (月〜金)", Create(1, 2, 3, 4, 5).DaysSummary);
        Assert.Equal("不明(7)", Create(7).DaysSummary);
        Assert.Equal("日・不明(7)", Create(0, 7).DaysSummary);
    }

    [Fact]
    public void EveryDayRequiresSevenDistinctValidWireDays()
    {
        Assert.Equal("毎日", Create(0, 1, 2, 3, 4, 5, 6).DaysSummary);
        Assert.Equal("月・火・水・木・金・土・不明(7)", Create(1, 2, 3, 4, 5, 6, 7).DaysSummary);
    }

    private static CloudAlarmGroupItemViewModel Create(params int[] days) =>
        new(new AlarmGroupDto("g", "summary", true, 1, null, "weekly", days, null, "Asia/Tokyo", false,
            Array.Empty<string>(), Array.Empty<AlarmTargetDto>()));
}
