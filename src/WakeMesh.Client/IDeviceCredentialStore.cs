namespace WakeMesh.Client;

/// <summary>
/// Persists the one-time device credential returned at registration.
/// Real MAUI secure storage is a later Mobile package — not this library.
/// </summary>
public interface IDeviceCredentialStore
{
    Task<string?> GetCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task SaveCredentialAsync(Guid deviceId, string credential, CancellationToken cancellationToken = default);

    Task DeleteCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default);
}
