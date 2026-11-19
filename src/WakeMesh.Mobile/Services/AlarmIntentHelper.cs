using System;
using System.Security.Cryptography;
using System.Text;

namespace WakeMesh.Mobile.Services;

public static class AlarmIntentHelper
{
    public const string Scheme = "wakemesh";
    public const string ActionAlarmTrigger = "wakemesh.mobile.ALARM_TRIGGER";
    public const string ActionSnoozeTrigger = "wakemesh.mobile.SNOOZE_TRIGGER";

    public static string CreateAlarmUri(string alarmId) =>
        $"{Scheme}://alarm/{alarmId}";

    public static string CreateSnoozeUri(string alarmId) =>
        $"{Scheme}://snooze/{alarmId}";

    public static string CreateDismissUri(string alarmId) =>
        $"{Scheme}://dismiss/{alarmId}";

    public static string CreateAlertUri(string alarmId) =>
        $"{Scheme}://alert/{alarmId}";

    public static int GetDeterministicRequestCode(string uriString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriString);
        var bytes = Encoding.UTF8.GetBytes(uriString);
        var hash = SHA256.HashData(bytes);
        // Positive 31-bit integer stable across all platforms and runs
        return BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
    }
}
