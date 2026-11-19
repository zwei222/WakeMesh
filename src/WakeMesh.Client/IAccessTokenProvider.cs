namespace WakeMesh.Client;

/// <summary>
/// Supplies the owner Microsoft access token for API calls.
/// MAUI/MSAL implementation is provided by a later Mobile package — not this library.
/// </summary>
public interface IAccessTokenProvider
{
    /// <returns>Bearer access token value without the "Bearer " prefix, or null if unavailable.</returns>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
