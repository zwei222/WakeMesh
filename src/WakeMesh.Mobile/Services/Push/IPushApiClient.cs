using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;

namespace WakeMesh.Mobile.Services.Push;

/// <summary>
/// Abstraction for server push-token endpoints so tests can verify registration/unregistration
/// without spinning up a live HTTP server or accessing internal client constructors.
/// </summary>
public interface IPushApiClient
{
    Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(Guid deviceId, string pushToken, bool notificationsCapable = false, CancellationToken cancellationToken = default);
    Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

internal sealed class DefaultPushApiClient : IPushApiClient
{
    private readonly WakeMeshApiClient _client;

    public DefaultPushApiClient(WakeMeshApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(Guid deviceId, string pushToken, bool notificationsCapable = false, CancellationToken cancellationToken = default)
        => _client.PutPushTokenAsync(deviceId, pushToken, notificationsCapable, cancellationToken);

    public Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => _client.DeletePushTokenAsync(deviceId, cancellationToken);
}
