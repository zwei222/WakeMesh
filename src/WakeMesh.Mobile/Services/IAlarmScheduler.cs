using System;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public interface IAlarmScheduler
{
    Task ScheduleAlarmAsync(LocalAlarmModel alarm, DateTimeOffset fireTime);
    Task CancelAlarmAsync(string alarmId);
}
