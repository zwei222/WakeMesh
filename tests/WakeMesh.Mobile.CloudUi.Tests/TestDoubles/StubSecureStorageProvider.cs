using System.Collections.Concurrent;
using System.Threading.Tasks;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.CloudUi.Tests.TestDoubles;

public sealed class StubSecureStorageProvider : ISecureStorageProvider
{
    private readonly ConcurrentDictionary<string, string> _storage = new();

    public Task<string?> GetAsync(string key)
    {
        _storage.TryGetValue(key, out var val);
        return Task.FromResult<string?>(val);
    }

    public Task SetAsync(string key, string value)
    {
        _storage[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key)
    {
        return Task.FromResult(_storage.TryRemove(key, out _));
    }

    public void RemoveAll()
    {
        _storage.Clear();
    }
}
