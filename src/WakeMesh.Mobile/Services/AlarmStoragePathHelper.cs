using System;
using System.IO;

namespace WakeMesh.Mobile.Services;

public static class AlarmStoragePathHelper
{
    public const string AlarmsFileName = "wakemesh_alarms.json";
    public const string DeviceIdFileName = "wakemesh_device_id.txt";
    public const string HolidayCacheFileName = "wakemesh_holidays.csv";
    public const string AuthSettingsFileName = "wakemesh_auth_settings.json";
    public const string MutexName = "Global\\WakeMesh_AlarmStorage_Mutex";

    public static string GetBaseStorageDirectory()
    {
        // Environment.SpecialFolder.LocalApplicationData provides the persistent app internal storage.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WakeMesh");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    public static string GetAlarmsJsonPath() =>
        Path.Combine(GetBaseStorageDirectory(), AlarmsFileName);

    public static string GetDeviceIdFilePath() =>
        Path.Combine(GetBaseStorageDirectory(), DeviceIdFileName);

    public static string GetHolidayCacheFilePath() =>
        Path.Combine(GetBaseStorageDirectory(), HolidayCacheFileName);

    public static string GetAuthSettingsFilePath() =>
        Path.Combine(GetBaseStorageDirectory(), AuthSettingsFileName);
}
