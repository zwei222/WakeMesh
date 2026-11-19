using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Core;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class AlarmStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public AlarmStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wakemesh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "alarms.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task GetAllAsync_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var store = new JsonFileAlarmStore(_filePath);
        var alarms = await store.GetAllAsync();
        Assert.Empty(alarms);
    }

    [Fact]
    public async Task SaveAsync_And_GetByIdAsync_PersistsAndRetrievesAlarm()
    {
        var store = new JsonFileAlarmStore(_filePath);
        var alarm = new LocalAlarmModel
        {
            Id = "alarm-1",
            Name = "朝の散歩",
            Time = new TimeOnly(6, 15),
            IsEnabled = true
        };

        await store.SaveAsync(alarm);

        var retrieved = await store.GetByIdAsync("alarm-1");
        Assert.NotNull(retrieved);
        Assert.Equal("朝の散歩", retrieved.Name);
        Assert.Equal(new TimeOnly(6, 15), retrieved.Time);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAlarm()
    {
        var store = new JsonFileAlarmStore(_filePath);
        var alarm = new LocalAlarmModel { Id = "alarm-del", Name = "削除対象" };
        await store.SaveAsync(alarm);

        await store.DeleteAsync("alarm-del");

        var retrieved = await store.GetByIdAsync("alarm-del");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task ToggleEnabledAsync_TogglesIsEnabled()
    {
        var store = new JsonFileAlarmStore(_filePath);
        var alarm = new LocalAlarmModel { Id = "alarm-toggle", Name = "切替対象", IsEnabled = true };
        await store.SaveAsync(alarm);

        var updated = await store.ToggleEnabledAsync("alarm-toggle");
        Assert.NotNull(updated);
        Assert.False(updated.IsEnabled);

        var retrieved = await store.GetByIdAsync("alarm-toggle");
        Assert.NotNull(retrieved);
        Assert.False(retrieved.IsEnabled);
    }
}
