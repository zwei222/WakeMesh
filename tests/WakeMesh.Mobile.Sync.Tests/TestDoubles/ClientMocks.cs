using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;

namespace WakeMesh.Mobile.Sync.Tests.TestDoubles;

public static class TestClientHelper
{
    public static WakeMeshApiClient CreateApiClient(
        HttpClient httpClient,
        IAccessTokenProvider tokens,
        IDeviceCredentialStore? credentials = null,
        bool ownsHttp = false)
    {
        var method = typeof(WakeMeshApiClient).GetMethod(
            "CreateForTests",
            BindingFlags.NonPublic | BindingFlags.Static,
            new[] { typeof(HttpClient), typeof(IAccessTokenProvider), typeof(IDeviceCredentialStore), typeof(bool) });

        if (method is null)
        {
            throw new InvalidOperationException("Could not find WakeMeshApiClient.CreateForTests via reflection.");
        }

        return (WakeMeshApiClient)method.Invoke(null, new object?[] { httpClient, tokens, credentials, ownsHttp })!;
    }
}

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
