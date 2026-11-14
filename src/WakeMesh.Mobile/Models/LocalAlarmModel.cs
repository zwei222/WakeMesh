using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WakeMesh.Core;

namespace WakeMesh.Mobile.Models;

public enum AlarmSyncStatus
{
    LocalOnly = 0,
    SyncPending = 1,
    Synced = 2,
    SyncError = 3
}

public enum AlarmRegistrationStatus
{
    Unregistered = 0,
    Scheduled = 1,
    Failed = 2
}

public sealed class LocalAlarmModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "アラーム";
    public bool IsEnabled { get; set; } = true;
    public TimeOnly Time { get; set; } = new(7, 0);
    public AlarmRepeatKind RepeatKind { get; set; } = AlarmRepeatKind.Weekly;
    public Weekdays ActiveWeekdays { get; set; } = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday;
    public DateOnly? OnceOnDate { get; set; }

    [JsonIgnore]
    public DateOnly? TargetDate
    {
        get => OnceOnDate;
        set => OnceOnDate = value;
    }

    [JsonIgnore]
    public DateOnly? ExplicitDate
    {
        get => OnceOnDate;
        set => OnceOnDate = value;
    }

    [JsonIgnore]
    public bool HasRegistrationError => OsRegistrationStatus == AlarmRegistrationStatus.Failed;
    public bool ExcludeHolidays { get; set; } = true;
    public bool SkipHolidays
    {
        get => ExcludeHolidays;
        set => ExcludeHolidays = value;
    }
    public int SnoozeMinutes { get; set; } = 5;
    public DateTimeOffset? SnoozeUntil { get; set; }
    public string DeviceId { get; set; } = "local-device";
    public string TimeZoneId { get; set; } = "Asia/Tokyo";
    public AlarmSyncStatus SyncStatus { get; set; } = AlarmSyncStatus.LocalOnly;
    public AlarmRegistrationStatus OsRegistrationStatus { get; set; } = AlarmRegistrationStatus.Unregistered;
    public string? OsRegistrationError { get; set; }
    public int VolumePercent { get; set; } = 100;
    public bool EnableVibration { get; set; } = true;
    public string? SoundUri { get; set; }
    public DateTimeOffset? NextFireTime { get; set; }
    public bool HasHolidayCoverageWarning { get; set; }

    public bool IsRegistrationFailed => OsRegistrationStatus == AlarmRegistrationStatus.Failed;
    public string RegistrationSummary => OsRegistrationStatus switch
    {
        AlarmRegistrationStatus.Scheduled => "OS登録済み",
        AlarmRegistrationStatus.Failed => "未登録 (権限エラー)",
        _ => "未登録"
    };

    public string DaysSummary
    {
        get
        {
            if (RepeatKind == AlarmRepeatKind.Once)
            {
                return OnceOnDate.HasValue
                    ? $"1回のみ ({OnceOnDate.Value:yyyy/MM/dd})"
                    : "1回のみ";
            }

            var allDays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday | Weekdays.Saturday | Weekdays.Sunday;
            var weekdays = Weekdays.Monday | Weekdays.Tuesday | Weekdays.Wednesday | Weekdays.Thursday | Weekdays.Friday;
            var weekend = Weekdays.Saturday | Weekdays.Sunday;

            if (ActiveWeekdays == allDays) return "毎日";
            if (ActiveWeekdays == weekdays) return "平日 (月〜金)";
            if (ActiveWeekdays == weekend) return "週末 (土・日)";
            if (ActiveWeekdays == Weekdays.None) return "繰り返しなし";

            var sb = new List<string>();
            if ((ActiveWeekdays & Weekdays.Monday) != 0) sb.Add("月");
            if ((ActiveWeekdays & Weekdays.Tuesday) != 0) sb.Add("火");
            if ((ActiveWeekdays & Weekdays.Wednesday) != 0) sb.Add("水");
            if ((ActiveWeekdays & Weekdays.Thursday) != 0) sb.Add("木");
            if ((ActiveWeekdays & Weekdays.Friday) != 0) sb.Add("金");
            if ((ActiveWeekdays & Weekdays.Saturday) != 0) sb.Add("土");
            if ((ActiveWeekdays & Weekdays.Sunday) != 0) sb.Add("日");

            return string.Join("・", sb);
        }
    }

    public AlarmDefinition ToAlarmDefinition()
    {
        return new AlarmDefinition
        {
            Id = Id,
            Name = Name,
            IsEnabled = IsEnabled,
            RepeatKind = RepeatKind,
            OnceOnDate = RepeatKind == AlarmRepeatKind.Once ? OnceOnDate : null,
            ActiveWeekdays = ActiveWeekdays,
            TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "Asia/Tokyo" : TimeZoneId,
            DeviceTimes = new List<DeviceLocalTime>
            {
                new(DeviceId, Time)
            },
            ExcludeHolidays = ExcludeHolidays,
            ExcludedDates = new HashSet<DateOnly>()
        };
    }

    public static LocalAlarmModel FromAlarmDefinition(AlarmDefinition definition, string deviceId)
    {
        var deviceTime = definition.DeviceTimes.FirstOrDefault(d => d.DeviceId == deviceId)
                         ?? definition.DeviceTimes.FirstOrDefault()
                         ?? new DeviceLocalTime(deviceId, new TimeOnly(7, 0));

        return new LocalAlarmModel
        {
            Id = definition.Id,
            Name = definition.Name,
            IsEnabled = definition.IsEnabled,
            Time = deviceTime.LocalTime,
            RepeatKind = definition.RepeatKind,
            OnceOnDate = definition.OnceOnDate,
            ActiveWeekdays = definition.ActiveWeekdays,
            ExcludeHolidays = definition.ExcludeHolidays,
            DeviceId = deviceId,
            TimeZoneId = string.IsNullOrWhiteSpace(definition.TimeZoneId) ? "Asia/Tokyo" : definition.TimeZoneId,
            SyncStatus = AlarmSyncStatus.LocalOnly
        };
    }
}
