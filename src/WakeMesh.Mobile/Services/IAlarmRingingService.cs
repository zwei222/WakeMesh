using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services;

public interface IAlarmRingingService
{
    bool HasActiveAlarms { get; }
    IReadOnlyList<ActiveRingingAlarm> GetRingingAlarms();
    event EventHandler? RingingStateChanged;
    Task<bool> DismissAlarmAsync(string alarmId);
    Task<bool> DismissAllAsync();
}
