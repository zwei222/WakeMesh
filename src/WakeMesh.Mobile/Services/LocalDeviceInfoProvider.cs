using System;
using System.IO;

namespace WakeMesh.Mobile.Services;

public sealed class LocalDeviceInfoProvider : IDeviceInfoProvider
{
    private static readonly object FileLock = new();
    public string DeviceId { get; }
    public string DeviceDisplayName => "この端末 (ローカル)";

    public LocalDeviceInfoProvider(string? explicitId = null, string? storageFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            DeviceId = explicitId;
            return;
        }

        var path = storageFilePath ?? AlarmStoragePathHelper.GetDeviceIdFilePath();
        DeviceId = GetOrCreatePersistedDeviceId(path);
    }

    private static string GetOrCreatePersistedDeviceId(string filePath)
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var existing = File.ReadAllText(filePath).Trim();
                    if (!string.IsNullOrWhiteSpace(existing) && Guid.TryParse(existing, out _))
                    {
                        return existing;
                    }
                }

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var newId = Guid.NewGuid().ToString("N");
                var tmpPath = filePath + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmpPath, newId);
                File.Move(tmpPath, filePath, overwrite: true);
                return newId;
            }
            catch (Exception)
            {
                // Fallback in-memory GUID if filesystem fails
                return Guid.NewGuid().ToString("N");
            }
        }
    }
}
