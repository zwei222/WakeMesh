using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public sealed class CorruptedAlarmStoreException : Exception
{
    public CorruptedAlarmStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class JsonFileAlarmStore : IAlarmStore
{
    private readonly string _filePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private SemaphoreSlim GetGate()
    {
        var normalized = Path.GetFullPath(_filePath);
        return PathGates.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
    }

    public JsonFileAlarmStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? AlarmStoragePathHelper.GetAlarmsJsonPath()
            : filePath;
    }

    public async Task<IReadOnlyList<LocalAlarmModel>> GetAllAsync()
    {
        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            return await LoadInternalAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalAlarmModel?> GetByIdAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            var list = await LoadInternalAsync();
            return list.FirstOrDefault(a => a.Id == id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(LocalAlarmModel alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            var list = (await LoadInternalAsync()).ToList();
            var existingIndex = list.FindIndex(a => a.Id == alarm.Id);
            if (existingIndex >= 0)
            {
                list[existingIndex] = alarm;
            }
            else
            {
                list.Add(alarm);
            }

            await SaveInternalAsync(list);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            var list = (await LoadInternalAsync()).ToList();
            var removedCount = list.RemoveAll(a => a.Id == id);
            if (removedCount > 0)
            {
                await SaveInternalAsync(list);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalAlarmModel?> ToggleEnabledAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            var list = (await LoadInternalAsync()).ToList();
            var alarm = list.FirstOrDefault(a => a.Id == id);
            if (alarm is null)
            {
                return null;
            }

            alarm.IsEnabled = !alarm.IsEnabled;
            await SaveInternalAsync(list);
            return alarm;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalAlarmModel?> UpdateAsync(string id, Action<LocalAlarmModel> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = GetGate();
        await gate.WaitAsync();
        try
        {
            var list = (await LoadInternalAsync()).ToList();
            var alarm = list.FirstOrDefault(a => a.Id == id);
            if (alarm is null)
            {
                return null;
            }

            mutate(alarm);
            await SaveInternalAsync(list);
            return alarm;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<LocalAlarmModel>> LoadInternalAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new List<LocalAlarmModel>();
        }

        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var result = await JsonSerializer.DeserializeAsync<List<LocalAlarmModel>>(stream, JsonOptions);
            return result ?? new List<LocalAlarmModel>();
        }
        catch (JsonException ex)
        {
            // Do not swallow corruption as an empty list to prevent overwriting user data
            throw new CorruptedAlarmStoreException(
                $"アラーム設定ファイル '{_filePath}' が破損しています。データを保護するため空リストでの上書きを拒否します。",
                ex);
        }
        catch (Exception ex) when (ex is not CorruptedAlarmStoreException)
        {
            throw new CorruptedAlarmStoreException(
                $"アラーム設定ファイル '{_filePath}' の読み込みに失敗しました。",
                ex);
        }
    }

    private async Task SaveInternalAsync(List<LocalAlarmModel> list)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, list, JsonOptions);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
