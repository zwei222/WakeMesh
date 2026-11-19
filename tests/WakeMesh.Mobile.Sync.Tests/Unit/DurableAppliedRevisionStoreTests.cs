using System;
using System.IO;
using System.Threading.Tasks;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

public sealed class DurableAppliedRevisionStoreTests : IDisposable
{
    private readonly string _testDir;

    public DurableAppliedRevisionStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-rev-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InitialStore_ReturnsNull_DistinguishingFromZero()
    {
        var devId = Guid.NewGuid();
        var binding = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var file = Path.Combine(_testDir, "rev.json");
        var store = new DurableAppliedRevisionStore(binding, file);

        var rev = await store.GetAppliedRevisionAsync(devId);
        Assert.Null(rev);
    }

    [Fact]
    public async Task Commit_PersistsAcrossInstances_SurvivesProcessRestart()
    {
        var devId = Guid.NewGuid();
        var binding = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var file = Path.Combine(_testDir, "rev.json");

        var store1 = new DurableAppliedRevisionStore(binding, file);
        await store1.CommitAppliedRevisionAsync(devId, 0);

        // Recreate instance as if after process restart
        var store2 = new DurableAppliedRevisionStore(binding, file);
        var rev = await store2.GetAppliedRevisionAsync(devId);
        Assert.Equal(0, rev);

        // Commit higher revision
        await store2.CommitAppliedRevisionAsync(devId, 42);

        var store3 = new DurableAppliedRevisionStore(binding, file);
        var rev3 = await store3.GetAppliedRevisionAsync(devId);
        Assert.Equal(42, rev3);
    }

    [Fact]
    public async Task Commit_IsMonotonic_LowerRevisionIsNoOp()
    {
        var devId = Guid.NewGuid();
        var binding = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var file = Path.Combine(_testDir, "rev.json");

        var store = new DurableAppliedRevisionStore(binding, file);
        await store.CommitAppliedRevisionAsync(devId, 10);
        Assert.Equal(10, await store.GetAppliedRevisionAsync(devId));

        // Attempt to commit lower revision
        await store.CommitAppliedRevisionAsync(devId, 5);

        // Must still be 10
        Assert.Equal(10, await store.GetAppliedRevisionAsync(devId));
    }

    [Fact]
    public async Task Store_EnforcesBindingAndDeviceId()
    {
        var devId = Guid.NewGuid();
        var binding = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var file = Path.Combine(_testDir, "rev.json");

        var store = new DurableAppliedRevisionStore(binding, file);
        await store.CommitAppliedRevisionAsync(devId, 5);

        // Wrong device ID throws ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAppliedRevisionAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CommitAppliedRevisionAsync(Guid.NewGuid(), 6));

        // Mismatched binding on same file throws InvalidOperationException
        var diffBinding = new CloudSyncBinding("https://api.example.com", "user-2", devId, 1);
        var diffStore = new DurableAppliedRevisionStore(diffBinding, file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => diffStore.GetAppliedRevisionAsync(devId));
    }

    [Fact]
    public async Task CorruptedFile_ThrowsInvalidOperationException_NeverSilentlyResets()
    {
        var devId = Guid.NewGuid();
        var binding = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var file = Path.Combine(_testDir, "rev.json");

        await File.WriteAllTextAsync(file, "!!NOT JSON!!");

        var store = new DurableAppliedRevisionStore(binding, file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAppliedRevisionAsync(devId));
    }
}
