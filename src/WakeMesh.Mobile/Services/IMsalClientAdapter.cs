using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services;

public interface IMsalClientAdapter
{
    Task<string?> AcquireTokenSilentAsync(string[] scopes, CancellationToken cancellationToken = default);
    Task<string?> AcquireTokenInteractiveAsync(string[] scopes, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<bool> HasAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the configured ClientId without blocking async operations.
    /// </summary>
    void UpdateClientId(string? clientId) { }

    /// <summary>
    /// Returns the currently bound home account identifier, if any.
    /// Never returns user email or display name to prevent accidental leakage.
    /// </summary>
    string? SelectedHomeAccountId => null;

    /// <summary>
    /// Restores the process-local selected account id from safe local persistence.
    /// Silent acquisition must use GetAccountAsync(this id) — never FirstOrDefault.
    /// </summary>
    void SetSelectedHomeAccountId(string? homeAccountId) { }
}
