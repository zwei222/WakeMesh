using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.Push;

public sealed class InMemoryPushTokenStore : IPushTokenStore
{
    private string? _token;
    private string? _bindingKey;

    public InMemoryPushTokenStore(string? initialToken = null)
    {
        _token = initialToken;
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_token);
    }

    public Task SaveTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        _token = token;
        if (string.IsNullOrEmpty(token))
        {
            _bindingKey = null;
        }

        return Task.CompletedTask;
    }

    public Task ClearTokenAsync(CancellationToken cancellationToken = default)
    {
        _token = null;
        _bindingKey = null;
        return Task.CompletedTask;
    }

    public Task<string?> GetBindingKeyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_bindingKey);

    public Task SaveTokenWithBindingAsync(string token, string? bindingKey, CancellationToken cancellationToken = default)
    {
        _token = token;
        _bindingKey = string.IsNullOrWhiteSpace(bindingKey) ? null : bindingKey.Trim();
        return Task.CompletedTask;
    }
}
