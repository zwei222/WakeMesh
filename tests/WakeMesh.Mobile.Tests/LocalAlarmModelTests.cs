using System;
using System.Collections.Generic;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class LocalAlarmModelTests
{
    [Fact]
    public void ToAlarmDefinition_ConvertsPropertiesAccurately()
    {
        var model = new LocalAlarmModel
        {
            Id = "alarm-123",
            Name = "朝の起床",
            IsEnabled = true,
            Time = new TimeOnly(7, 30),
            RepeatKind = AlarmRepeatKind.Weekly,
            ActiveWeekdays = Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday,
            ExcludeHolidays = true,
            SnoozeMinutes = 5,
            DeviceId = "device-abc",
            TimeZoneId = "Asia/Tokyo"
        };

        var definition = model.ToAlarmDefinition();

        Assert.Equal("alarm-123", definition.Id);
        Assert.Equal("朝の起床", definition.Name);
        Assert.True(definition.IsEnabled);
        Assert.Equal(AlarmRepeatKind.Weekly, definition.RepeatKind);
        Assert.Equal(Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday, definition.ActiveWeekdays);
        Assert.True(definition.ExcludeHolidays);
        Assert.Equal("Asia/Tokyo", definition.TimeZoneId);
        Assert.Single(definition.DeviceTimes);
        Assert.Equal("device-abc", definition.DeviceTimes[0].DeviceId);
        Assert.Equal(new TimeOnly(7, 30), definition.DeviceTimes[0].LocalTime);
    }
}
