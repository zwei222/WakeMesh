using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.CloudUi.Tests.TestDoubles;

public sealed class MockAuthService : IAuthService
{
    private AuthState _state = AuthState.SignedIn;
    private string _statusMessage = "サインイン完了";
    private AuthSettings _settings = new();
    private AccountBinding _currentBinding;
    private string? _accessToken;

    public AuthState State => _state;
    public string StatusMessage => _statusMessage;
    public bool CanSignIn => _state == AuthState.SignedOut;
    public bool CanSignOut => _state == AuthState.SignedIn;
    public AuthSettings Settings => _settings;
    public AccountBinding CurrentBinding => _currentBinding;

    public event EventHandler? StateChanged;

    public MockAuthService(string apiBaseUrl, string accessToken, string? homeAccountId = "test-owner-account-id")
    {
        _accessToken = accessToken;
        _settings = new AuthSettings
        {
            ApiBaseUrl = apiBaseUrl,
            AndroidClientId = Guid.NewGuid().ToString("D"),
            ApiClientId = Guid.NewGuid().ToString("D")
        };
        _currentBinding = new AccountBinding(apiBaseUrl, _settings.ApiClientId, _settings.AndroidClientId, homeAccountId);
    }

    public void SetState(AuthState state, string? message = null)
    {
        _state = state;
        _statusMessage = message ?? state.ToString();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAccessToken(string? token)
    {
        _accessToken = token;
    }

    public void SetSettings(AuthSettings settings, string? homeAccountId = "test-owner-account-id")
    {
        _settings = settings;
        _currentBinding = new AccountBinding(
            settings.ApiBaseUrl ?? string.Empty,
            settings.ApiClientId ?? string.Empty,
            settings.AndroidClientId ?? string.Empty,
            homeAccountId);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_accessToken);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReloadSettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ApplySettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default)
    {
        SetSettings(settings);
        return Task.CompletedTask;
    }

    public Task ClearSettingsAsync(CancellationToken cancellationToken = default)
    {
        _settings = new AuthSettings();
        _currentBinding = new AccountBinding();
        SetState(AuthState.NotConfigured, "未設定");
        return Task.CompletedTask;
    }

    public Task<bool> SignInInteractiveAsync(CancellationToken cancellationToken = default)
    {
        SetState(AuthState.SignedIn, "サインイン完了");
        return Task.FromResult(true);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SetState(AuthState.SignedOut, "サインアウト済み");
        return Task.CompletedTask;
    }

    public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ConnectionCheckResult.Success("接続成功"));
}

public static class TestClientHelper
{
    public static WakeMeshApiClient CreateApiClient(
        System.Net.Http.HttpClient httpClient,
        IAccessTokenProvider tokens,
        IDeviceCredentialStore? credentials = null,
        bool ownsHttp = false)
    {
        var method = typeof(WakeMeshApiClient).GetMethod(
            "CreateForTests",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            new[] { typeof(System.Net.Http.HttpClient), typeof(IAccessTokenProvider), typeof(IDeviceCredentialStore), typeof(bool) });

        if (method is null)
        {
            throw new InvalidOperationException("Could not find WakeMeshApiClient.CreateForTests via reflection.");
        }

        return (WakeMeshApiClient)method.Invoke(null, new object?[] { httpClient, tokens, credentials, ownsHttp })!;
    }
}
