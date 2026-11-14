using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;

namespace WakeMesh.Mobile.Services.CloudSync;

internal sealed class AppliedRevisionRecord
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public Guid ServerDeviceId { get; set; }
    public long RegistrationGeneration { get; set; }
    public long AppliedRevision { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
}

/// <summary>
/// Durable, process-restart surviving implementation of <see cref="IAppliedRevisionStore"/>.
/// Strictly bound to an immutable <see cref="CloudSyncBinding"/>.
/// Uses full-path shared gate, atomic file replacement (temp + flush(true) + rename),
/// and enforces monotonic revision commits.
/// </summary>
public sealed class DurableAppliedRevisionStore : IAppliedRevisionStore
{
    private readonly CloudSyncBinding _binding;
    private readonly string _filePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath => _filePath;

    public DurableAppliedRevisionStore(CloudSyncBinding binding, string? filePath = null)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var baseDir = AlarmStoragePathHelper.GetBaseStorageDirectory();
            _filePath = Path.Combine(baseDir, $"applied_revision_{_binding.ToStorageKey()}.json");
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

    public async Task<long?> GetAppliedRevisionAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId != _binding.ServerDeviceId)
        {
            throw new ArgumentException(
                $"Requested deviceId {deviceId} does not match store binding ServerDeviceId {_binding.ServerDeviceId}",
                nameof(deviceId));
        }

        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                throw new InvalidOperationException($"Failed to read applied revision store at {_filePath}", ex);
            }

            AppliedRevisionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<AppliedRevisionRecord>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Malformed applied revision JSON at {_filePath}", ex);
            }

            if (record is null)
            {
                throw new InvalidOperationException($"Applied revision record at {_filePath} was null.");
            }

            if (record.ServerDeviceId != _binding.ServerDeviceId ||
                record.RegistrationGeneration != _binding.RegistrationGeneration ||
                !string.Equals(record.ApiBaseUrl, _binding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.AccountId, _binding.AccountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Applied revision binding mismatch at {_filePath}. " +
                    $"Expected dev={_binding.ServerDeviceId},gen={_binding.RegistrationGeneration},acc={_binding.AccountId},url={_binding.ApiBaseUrl}; " +
                    $"Found dev={record.ServerDeviceId},gen={record.RegistrationGeneration},acc={record.AccountId},url={record.ApiBaseUrl}");
            }

            return record.AppliedRevision;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CommitAppliedRevisionAsync(Guid deviceId, long revision, CancellationToken cancellationToken = default)
    {
        if (deviceId != _binding.ServerDeviceId)
        {
            throw new ArgumentException(
                $"Requested deviceId {deviceId} does not match store binding ServerDeviceId {_binding.ServerDeviceId}",
                nameof(deviceId));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision cannot be negative.");
        }

        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                var existingJson = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                var existing = JsonSerializer.Deserialize<AppliedRevisionRecord>(existingJson);
                if (existing is not null)
                {
                    if (existing.ServerDeviceId != _binding.ServerDeviceId ||
                        existing.RegistrationGeneration != _binding.RegistrationGeneration ||
                        !string.Equals(existing.ApiBaseUrl, _binding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(existing.AccountId, _binding.AccountId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Applied revision store binding mismatch during commit at {_filePath}.");
                    }

                    // Monotonicity: ignore or no-op commits where revision is less than stored value
                    if (revision < existing.AppliedRevision)
                    {
                        return;
                    }
                }
            }

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var record = new AppliedRevisionRecord
            {
                ApiBaseUrl = _binding.ApiBaseUrl,
                AccountId = _binding.AccountId,
                ServerDeviceId = _binding.ServerDeviceId,
                RegistrationGeneration = _binding.RegistrationGeneration,
                AppliedRevision = revision,
                CommittedAt = DateTimeOffset.UtcNow
            };

            var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(record, JsonOptions);

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

    /// <summary>
    /// Removes the durable applied high-water mark for this binding (used when the binding scope is
    /// retired). A missing file is not an error. Failures propagate so callers can report them honestly.
    /// </summary>
    public async Task ClearAppliedRevisionAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
