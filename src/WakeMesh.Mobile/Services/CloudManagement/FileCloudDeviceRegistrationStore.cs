using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Durable file-based implementation of <see cref="ICloudDeviceRegistrationStore"/>.
/// Saves registration state into no-backup app data directory with atomic file replacement.
/// </summary>
public sealed class FileCloudDeviceRegistrationStore : ICloudDeviceRegistrationStore
{
    public const string DefaultFileName = "wakemesh_cloud_device_registration.json";

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath => _filePath;

    public FileCloudDeviceRegistrationStore(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
            _filePath = Path.Combine(baseDir, DefaultFileName);
        }
        else
        {
            _filePath = filePath;
        }
    }

    public async Task<CloudDeviceRegistration?> LoadRegistrationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Failed to read device registration file at {_filePath}", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<CloudDeviceRegistration>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Malformed device registration JSON at {_filePath}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveRegistrationAsync(CloudDeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(registration, JsonOptions);
            var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearRegistrationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
