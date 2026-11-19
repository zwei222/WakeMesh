using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class MauiSecureStorageProvider : ISecureStorageProvider
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task<bool> RemoveAsync(string key) => Task.FromResult(SecureStorage.Default.Remove(key));

    public void RemoveAll() => SecureStorage.Default.RemoveAll();
}
