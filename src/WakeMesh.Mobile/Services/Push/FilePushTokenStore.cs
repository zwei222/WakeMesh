using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.Push;

public sealed class FilePushTokenStore : IPushTokenStore
{
    private readonly string _filePath;
    private readonly string _bindingPath;
    private readonly object _lock = new();

    public FilePushTokenStore(string? filePath = null)
    {
        if (filePath != null)
        {
            _filePath = filePath;
        }
        else
        {
            string baseDir;
#if ANDROID
            try
            {
                var context = global::Android.App.Application.Context;
                if (context?.NoBackupFilesDir != null)
                {
                    baseDir = context.NoBackupFilesDir.AbsolutePath;
                }
                else
                {
                    baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
                }
            }
            catch
            {
                baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
            }
#else
            baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
#endif
            _filePath = Path.Combine(baseDir, "push_token.dat");
        }

        _bindingPath = _filePath + ".binding";
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                var content = File.ReadAllText(_filePath).Trim();
                return Task.FromResult<string?>(string.IsNullOrEmpty(content) ? null : content);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task<string?> GetBindingKeyAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!File.Exists(_bindingPath))
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                var content = File.ReadAllText(_bindingPath).Trim();
                return Task.FromResult<string?>(string.IsNullOrEmpty(content) ? null : content);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task SaveTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (string.IsNullOrEmpty(token))
                {
                    DeleteIfExists(_filePath);
                    DeleteIfExists(_bindingPath);
                }
                else
                {
                    var tempFile = _filePath + ".tmp";
                    File.WriteAllText(tempFile, token);
                    File.Move(tempFile, _filePath, overwrite: true);
                }
            }
            catch
            {
                // Never leak token or fail unhandled
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveTokenWithBindingAsync(string token, string? bindingKey, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var tempFile = _filePath + ".tmp";
                File.WriteAllText(tempFile, token);
                File.Move(tempFile, _filePath, overwrite: true);

                if (string.IsNullOrWhiteSpace(bindingKey))
                {
                    DeleteIfExists(_bindingPath);
                }
                else
                {
                    var tempBinding = _bindingPath + ".tmp";
                    File.WriteAllText(tempBinding, bindingKey.Trim());
                    File.Move(tempBinding, _bindingPath, overwrite: true);
                }
            }
            catch
            {
                // Never leak token or fail unhandled
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearTokenAsync(CancellationToken cancellationToken = default)
    {
        return SaveTokenAsync(null, cancellationToken);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
