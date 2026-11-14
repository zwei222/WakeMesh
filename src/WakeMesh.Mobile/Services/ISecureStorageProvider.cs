using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Abstraction for platform secure key-value storage (e.g. Android KeyStore / MAUI SecureStorage).
/// </summary>
public interface ISecureStorageProvider
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<bool> RemoveAsync(string key);
    void RemoveAll();
}
