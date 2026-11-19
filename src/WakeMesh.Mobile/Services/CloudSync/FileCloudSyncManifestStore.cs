using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.CloudSync;

public sealed class FileCloudSyncManifestStore : ICloudSyncManifestStore
{
    private readonly CloudSyncBinding _binding;
    private readonly string _filePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath => _filePath;

    public FileCloudSyncManifestStore(CloudSyncBinding binding, string? filePath = null)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
            _filePath = Path.Combine(baseDir, $"sync_manifest_{_binding.ToStorageKey()}.json");
        }
        else
        {
            _filePath = filePath;
        }
    }

    private SemaphoreSlim GetGate()
    {
        var normalized = Path.GetFullPath(_filePath);
        return PathGates.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CloudSyncManifest> LoadAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new CloudSyncManifest
                {
                    ApiBaseUrl = _binding.ApiBaseUrl,
                    AccountId = _binding.AccountId,
                    ServerDeviceId = _binding.ServerDeviceId,
                    RegistrationGeneration = _binding.RegistrationGeneration
                };
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CorruptedSyncManifestException($"Failed to read sync manifest at {_filePath}", ex);
            }

            CloudSyncManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<CloudSyncManifest>(json);
            }
            catch (JsonException ex)
            {
                throw new CorruptedSyncManifestException($"Malformed sync manifest JSON at {_filePath}", ex);
            }

            if (manifest is null)
            {
                throw new CorruptedSyncManifestException($"Sync manifest at {_filePath} contained null object.");
            }

            // Validate binding integrity
            if (manifest.ServerDeviceId != _binding.ServerDeviceId ||
                manifest.RegistrationGeneration != _binding.RegistrationGeneration ||
                !string.Equals(manifest.ApiBaseUrl, _binding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.AccountId, _binding.AccountId, StringComparison.Ordinal))
            {
                throw new CorruptedSyncManifestException(
                    $"Sync manifest binding mismatch at {_filePath}. " +
                    $"Expected dev={_binding.ServerDeviceId},gen={_binding.RegistrationGeneration},acc={_binding.AccountId},url={_binding.ApiBaseUrl}; " +
                    $"Found dev={manifest.ServerDeviceId},gen={manifest.RegistrationGeneration},acc={manifest.AccountId},url={manifest.ApiBaseUrl}");
            }

            manifest.Schedules ??= new();
            return manifest;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(CloudSyncManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.ServerDeviceId != _binding.ServerDeviceId ||
            manifest.RegistrationGeneration != _binding.RegistrationGeneration ||
            !string.Equals(manifest.ApiBaseUrl, _binding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.AccountId, _binding.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot save sync manifest with mismatched binding parameters.");
        }

        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(manifest, JsonOptions);

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(fs, Encoding.UTF8);
                await writer.WriteAsync(json).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }
}
