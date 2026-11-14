using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.Push;

public interface IPushTokenStore
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    Task SaveTokenAsync(string? token, CancellationToken cancellationToken = default);
    Task ClearTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional binding-scope metadata for the durable token. Default is absent (null).
    /// </summary>
    Task<string?> GetBindingKeyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Persists the token together with a binding key so a later session-end can clear only matching scope.
    /// Default implementation stores the token only.
    /// </summary>
    Task SaveTokenWithBindingAsync(string token, string? bindingKey, CancellationToken cancellationToken = default)
        => SaveTokenAsync(token, cancellationToken);
}
