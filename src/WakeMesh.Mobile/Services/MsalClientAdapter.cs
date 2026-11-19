using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Production MSAL adapter wrapping IPublicClientApplication.
/// Uses system browser (broker-less, WithUseEmbeddedWebView(false)), consumers authority,
/// compile-time redirect URI, and delegates token caching strictly to MSAL.
/// Ensures single-account binding without FirstOrDefault fallback, explicitly removes accounts on SignOut,
/// and never exposes user personal info (email, UPN) to logs or state.
/// </summary>
public sealed class MsalClientAdapter : IMsalClientAdapter
{
    private readonly Func<object?>? _parentActivityProvider;
    private readonly Func<string>? _legacyClientIdProvider;
    private IPublicClientApplication? _pca;
    private string? _currentClientId;
    private string? _selectedHomeAccountId;
    private readonly object _lock = new();

    public string? SelectedHomeAccountId => _selectedHomeAccountId;

    public MsalClientAdapter(string? initialClientId = null, Func<object?>? parentActivityProvider = null)
    {
        _currentClientId = AccountBinding.NormalizeGuid(initialClientId);
#if ANDROID
        _parentActivityProvider = parentActivityProvider ?? (() => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity);
#else
        _parentActivityProvider = parentActivityProvider;
#endif
    }

    /// <summary>
    /// Legacy constructor maintained for backward compatibility. Non-blocking callers only.
    /// </summary>
    public MsalClientAdapter(Func<string> clientIdProvider, Func<object?>? parentActivityProvider = null)
        : this(initialClientId: null, parentActivityProvider)
    {
        _legacyClientIdProvider = clientIdProvider ?? throw new ArgumentNullException(nameof(clientIdProvider));
    }

    public void UpdateClientId(string? clientId)
    {
        var normalized = AccountBinding.NormalizeGuid(clientId);
        lock (_lock)
        {
            if (_currentClientId != normalized)
            {
                _currentClientId = normalized;
                // Invalidate PCA so new clientId rebuilds it. Selected account is owned by AuthService persistence.
                _pca = null;
            }
        }
    }

    public void SetSelectedHomeAccountId(string? homeAccountId)
    {
        lock (_lock)
        {
            _selectedHomeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId.Trim();
        }
    }

    private IPublicClientApplication GetClientApplication()
    {
        var clientId = _currentClientId;
        if (string.IsNullOrWhiteSpace(clientId) && _legacyClientIdProvider != null)
        {
            clientId = AccountBinding.NormalizeGuid(_legacyClientIdProvider());
            _currentClientId = clientId;
        }

        if (string.IsNullOrWhiteSpace(clientId) || AccountBinding.IsZeroOrEmptyGuid(clientId))
        {
            throw new InvalidOperationException("ClientId is empty or zero GUID placeholder. Cannot configure MSAL client application.");
        }

        lock (_lock)
        {
            if (_pca != null)
            {
                return _pca;
            }

            var builder = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, "consumers")
                .WithRedirectUri(CompiledAuthConstants.CompileTimeRedirectUri);

#if ANDROID
            if (_parentActivityProvider != null)
            {
                builder = builder.WithParentActivityOrWindow(_parentActivityProvider);
            }
#endif

            _pca = builder.Build();
            return _pca;
        }
    }

    public async Task<string?> AcquireTokenSilentAsync(string[] scopes, CancellationToken cancellationToken = default)
    {
        var app = GetClientApplication();

        // Security rule: multiple account FirstOrDefault is strictly forbidden.
        // Silent token acquisition MUST use the exact bound account previously selected interactively.
        string? selectedId;
        lock (_lock)
        {
            selectedId = _selectedHomeAccountId;
        }

        if (string.IsNullOrEmpty(selectedId))
        {
            throw new MsalUiRequiredException("user_null", "No account previously authenticated. Interactive sign-in is required.");
        }

        var account = await app.GetAccountAsync(selectedId);
        if (account == null)
        {
            throw new MsalUiRequiredException("user_null", "Bound account is not present in MSAL cache. Interactive sign-in is required.");
        }

        var result = await app.AcquireTokenSilent(scopes, account)
            .ExecuteAsync(cancellationToken);

        return result?.AccessToken;
    }

    public async Task<string?> AcquireTokenInteractiveAsync(string[] scopes, CancellationToken cancellationToken = default)
    {
        var app = GetClientApplication();
        var builder = app.AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount);

#if ANDROID
        if (_parentActivityProvider != null)
        {
            builder = builder.WithParentActivityOrWindow(_parentActivityProvider);
        }
#endif

        var result = await builder.ExecuteAsync(cancellationToken);

        // Bind selected account identifier
        if (result?.Account?.HomeAccountId?.Identifier != null)
        {
            SetSelectedHomeAccountId(result.Account.HomeAccountId.Identifier);
        }

        return result?.AccessToken;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        IPublicClientApplication? app = null;
        lock (_lock)
        {
            app = _pca;
        }

        if (app == null)
        {
            try
            {
                app = GetClientApplication();
            }
            catch
            {
                // If settings were cleared or unconfigured, app cannot be built, but that is safe for signOut.
            }
        }

        if (app != null)
        {
            var accounts = (await app.GetAccountsAsync()).ToList();
            foreach (var account in accounts)
            {
                await app.RemoveAsync(account);
            }
        }

        SetSelectedHomeAccountId(null);
    }

    public async Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? selectedId;
            lock (_lock)
            {
                selectedId = _selectedHomeAccountId;
            }

            if (string.IsNullOrEmpty(selectedId))
            {
                return false;
            }

            var app = GetClientApplication();
            var account = await app.GetAccountAsync(selectedId);
            return account != null;
        }
        catch
        {
            return false;
        }
    }
}
