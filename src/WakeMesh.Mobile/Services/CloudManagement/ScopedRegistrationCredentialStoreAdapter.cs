using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Credential store adapter that locks in an immutable AccountBinding for registration,
/// ensuring that even if settings or active account change in-flight, credentials are saved
/// under the exact binding scope established when registration commenced.
/// </summary>
public sealed class ScopedRegistrationCredentialStoreAdapter : IDeviceCredentialStore
{
    private readonly IDeviceCredentialStore? _innerStore;
    private readonly AccountBinding _immutableBinding;
    private readonly ISecureStorageProvider? _secureStorage;

    public AccountBinding ImmutableBinding => _immutableBinding;

    public ScopedRegistrationCredentialStoreAdapter(
        IDeviceCredentialStore? innerStore,
        AccountBinding immutableBinding)
    {
        _innerStore = innerStore;
        _immutableBinding = immutableBinding ?? throw new ArgumentNullException(nameof(immutableBinding));

        if (innerStore is SecureDeviceCredentialStore secureStore)
        {
            var field = typeof(SecureDeviceCredentialStore).GetField("_secureStorage", BindingFlags.Instance | BindingFlags.NonPublic);
            _secureStorage = field?.GetValue(secureStore) as ISecureStorageProvider;
        }
    }

    public async Task SaveCredentialAsync(Guid deviceId, string credential, CancellationToken cancellationToken = default)
    {
        if (_secureStorage != null)
        {
            var fixedStore = new SecureDeviceCredentialStore(_secureStorage, () => _immutableBinding);
            await fixedStore.SaveCredentialAsync(deviceId, credential, cancellationToken).ConfigureAwait(false);
        }
        else if (_innerStore != null)
        {
            await _innerStore.SaveCredentialAsync(deviceId, credential, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<string?> GetCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (_secureStorage != null)
        {
            var fixedStore = new SecureDeviceCredentialStore(_secureStorage, () => _immutableBinding);
            return fixedStore.GetCredentialAsync(deviceId, cancellationToken);
        }
        return _innerStore?.GetCredentialAsync(deviceId, cancellationToken) ?? Task.FromResult<string?>(null);
    }

    public Task DeleteCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (_secureStorage != null)
        {
            var fixedStore = new SecureDeviceCredentialStore(_secureStorage, () => _immutableBinding);
            return fixedStore.DeleteCredentialAsync(deviceId, cancellationToken);
        }
        return _innerStore?.DeleteCredentialAsync(deviceId, cancellationToken) ?? Task.CompletedTask;
    }
}
