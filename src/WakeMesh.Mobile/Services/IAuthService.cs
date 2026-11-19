using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public enum AuthState
{
    NotConfigured,
    NeedsRebuild,
    SignedOut,
    SigningIn,
    SignedIn,
    ReauthRequired
}

public sealed record ConnectionCheckResult(bool IsConnected, string Message, int? StatusCode = null)
{
    public static ConnectionCheckResult Unconfigured(string message) => new(false, message);
    public static ConnectionCheckResult Success(string message, int statusCode = 200) => new(true, message, statusCode);
    public static ConnectionCheckResult Failed(string message, int? statusCode = null) => new(false, message, statusCode);
}

public interface IAuthService : IAccessTokenProvider
{
    AuthState State { get; }
    string StatusMessage { get; }
    bool CanSignIn { get; }
    bool CanSignOut { get; }
    AuthSettings Settings { get; }
    AccountBinding CurrentBinding { get; }

    event EventHandler? StateChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ReloadSettingsAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default);
    Task ClearSettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default);
}
