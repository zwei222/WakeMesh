using System;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public sealed class AlarmScheduleCalculator
{
    private readonly IHolidayService _holidayService;
    private readonly NextFireCalculator _coreCalculator;

    public AlarmScheduleCalculator(IHolidayService holidayService, NextFireCalculator? coreCalculator = null)
    {
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _coreCalculator = coreCalculator ?? new NextFireCalculator();
    }

    /// <summary>
    /// Calculates the next fire time for the specified alarm.
    /// Distinguishes valid null (alarm disabled or one-off expired) from ArgumentException (corrupt/invalid config).
    /// </summary>
    public NextFire? CalculateNextFire(LocalAlarmModel alarm, DateTimeOffset? afterExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        if (!alarm.IsEnabled)
        {
            alarm.HasHolidayCoverageWarning = false;
            return null;
        }

        var definition = alarm.ToAlarmDefinition();
        var after = afterExclusive ?? DateTimeOffset.Now;

        NextFire? result;
        if (alarm.ExcludeHolidays && _holidayService.Calendar is not null)
        {
            result = _coreCalculator.GetNextFire(
                alarm: definition,
                deviceId: alarm.DeviceId,
                afterExclusive: after,
                holidayCalendar: _holidayService.Calendar,
                out var warning);
            alarm.HasHolidayCoverageWarning = warning;
        }
        else
        {
            var holidays = alarm.ExcludeHolidays ? _holidayService.GetHolidays() : null;
            result = _coreCalculator.GetNextFire(
                alarm: definition,
                deviceId: alarm.DeviceId,
                afterExclusive: after,
                holidays: holidays);
            // If holidays were requested but calendar was missing, flag coverage warning
            alarm.HasHolidayCoverageWarning = alarm.ExcludeHolidays && !_holidayService.HasHolidayData;
        }

        return result;
    }
}
