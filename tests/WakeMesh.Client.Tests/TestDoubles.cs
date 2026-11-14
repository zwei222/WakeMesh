using System.Collections.Concurrent;

namespace WakeMesh.Client.Tests;

public sealed class StaticAccessTokenProvider : IAccessTokenProvider
{
    private string? _token;

    public StaticAccessTokenProvider(string? token = null) => _token = token;

    public void SetToken(string? token) => _token = token;

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_token);
}

public sealed class MemoryDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly ConcurrentDictionary<Guid, string> _map = new();

    public Task<string?> GetCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_map.TryGetValue(deviceId, out var c) ? c : null);

    public Task SaveCredentialAsync(Guid deviceId, string credential, CancellationToken cancellationToken = default)
    {
        _map[deviceId] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        _map.TryRemove(deviceId, out _);
        return Task.CompletedTask;
    }

    public void Set(Guid deviceId, string credential) => _map[deviceId] = credential;
}
