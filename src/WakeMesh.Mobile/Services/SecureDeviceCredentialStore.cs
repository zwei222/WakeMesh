using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Exception thrown when device credentials cannot be decrypted or are corrupted in secure storage.
/// In accordance with security policy, plaintext fallback is forbidden and re-registration is required.
/// </summary>
public sealed class DeviceCredentialCorruptedException : Exception
{
    public Guid DeviceId { get; }

    public DeviceCredentialCorruptedException(Guid deviceId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}

/// <summary>
/// Thrown when the binding-scoped server-device-id index cannot be read. Without the index the store
/// cannot prove that every credential of a binding was removed, so the caller must not claim success.
/// </summary>
public sealed class CredentialIndexCorruptedException : Exception
{
    public CredentialIndexCorruptedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Persists deviceId and credential as a pair in platform secure storage.
/// Strictly scoped by structured AccountBinding (API origin + API client ID + Android client ID + HomeAccountId).
/// Forbids sending credentials to a mismatched API origin or across accounts.
/// Forbids plaintext fallback; requires re-registration on decryption/integrity failures.
/// Also maintains a binding-scoped index of every server device id registered under the binding, so a
/// sign-out/settings change can purge all of them (the local device id is not the credential key).
/// </summary>
public sealed class SecureDeviceCredentialStore : IDeviceCredentialStore
{
    private const string KeyPrefix = "wakemesh_device_cred_";
    private const string IndexKeyPrefix = "wakemesh_device_cred_index_";
    private const int IndexSchemaVersion = 1;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexGates = new(StringComparer.Ordinal);

    private readonly ISecureStorageProvider _secureStorage;
    private readonly Func<AccountBinding?>? _bindingProvider;

    public SecureDeviceCredentialStore(
        ISecureStorageProvider secureStorage,
        Func<AccountBinding?>? bindingProvider = null)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _bindingProvider = bindingProvider;
    }

    private static string FormatKey(Guid deviceId, AccountBinding? binding)
    {
        if (binding != null && !string.IsNullOrEmpty(binding.ApiOrigin))
        {
            return $"{KeyPrefix}{binding.GetBindingKey()}_{deviceId:D}";
        }

        return $"{KeyPrefix}{deviceId:D}";
    }

    private string GetKey(Guid deviceId) => FormatKey(deviceId, _bindingProvider?.Invoke());

    private sealed class StoredCredentialPair
    {
        public Guid DeviceId { get; set; }
        public string Credential { get; set; } = string.Empty;
        public string? ApiOrigin { get; set; }
        public string? ApiClientId { get; set; }
        public string? AndroidClientId { get; set; }
        public string? HomeAccountId { get; set; }
    }

    private sealed class CredentialIndexDocument
    {
        public int SchemaVersion { get; set; } = IndexSchemaVersion;
        public List<string> ServerDeviceIds { get; set; } = new();
    }

    private static string? GetIndexKey(AccountBinding? binding)
        => binding != null && !string.IsNullOrEmpty(binding.ApiOrigin)
            ? $"{IndexKeyPrefix}{binding.GetBindingKey()}"
            : null;

    private static SemaphoreSlim GetIndexGate(string indexKey)
        => IndexGates.GetOrAdd(indexKey, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Reads the binding index. A malformed index raises <see cref="CredentialIndexCorruptedException"/>
    /// instead of pretending there is nothing to clear.
    /// </summary>
    private async Task<List<Guid>> ReadIndexAsync(string indexKey)
    {
        string? raw;
        try
        {
            raw = await _secureStorage.GetAsync(indexKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CredentialIndexCorruptedException("端末credential索引の読み込みに失敗しました。", ex);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<Guid>();
        }

        CredentialIndexDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CredentialIndexDocument>(raw);
        }
        catch (Exception ex)
        {
            throw new CredentialIndexCorruptedException("端末credential索引の形式が破損しています。", ex);
        }

        if (doc?.ServerDeviceIds is null)
        {
            throw new CredentialIndexCorruptedException("端末credential索引の形式が破損しています。");
        }

        var ids = new List<Guid>(doc.ServerDeviceIds.Count);
        foreach (var value in doc.ServerDeviceIds)
        {
            if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            {
                throw new CredentialIndexCorruptedException("端末credential索引に不正なdeviceIdが含まれています。");
            }

            if (!ids.Contains(parsed))
            {
                ids.Add(parsed);
            }
        }

        return ids;
    }

    private Task WriteIndexAsync(string indexKey, IReadOnlyCollection<Guid> deviceIds)
    {
        var doc = new CredentialIndexDocument
        {
            ServerDeviceIds = deviceIds.Select(id => id.ToString("D")).ToList(),
        };

        return _secureStorage.SetAsync(indexKey, JsonSerializer.Serialize(doc));
    }

    public async Task<string?> GetCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(deviceId);
        string? raw;
        try
        {
            raw = await _secureStorage.GetAsync(key);
        }
        catch (Exception ex)
        {
            try
            {
                await _secureStorage.RemoveAsync(key);
            }
            catch
            {
                // Ignore cleanup error
            }
            throw new DeviceCredentialCorruptedException(
                deviceId,
                "端末credentialの復号に失敗しました。平文フォールバックは禁止されているため、端末の再登録が必要です。",
                ex);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        StoredCredentialPair? pair;
        try
        {
            pair = JsonSerializer.Deserialize<StoredCredentialPair>(raw);
        }
        catch (Exception ex)
        {
            try
            {
                await _secureStorage.RemoveAsync(key);
            }
            catch
            {
                // Ignore cleanup error
            }
            throw new DeviceCredentialCorruptedException(
                deviceId,
                "端末credentialの形式が破損しています。平文フォールバックは禁止されているため、端末の再登録が必要です。",
                ex);
        }

        if (pair == null || pair.DeviceId != deviceId || string.IsNullOrEmpty(pair.Credential))
        {
            try
            {
                await _secureStorage.RemoveAsync(key);
            }
            catch
            {
                // Ignore cleanup error
            }
            throw new DeviceCredentialCorruptedException(
                deviceId,
                "端末credentialとサーバーdeviceIdの組が不整合です。平文フォールバックは禁止されているため、端末の再登録が必要です。");
        }

        // Validate account/origin binding matches current binding context
        var currentBinding = _bindingProvider?.Invoke();
        if (currentBinding != null && !string.IsNullOrEmpty(currentBinding.ApiOrigin))
        {
            if (!string.Equals(pair.ApiOrigin, currentBinding.ApiOrigin, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pair.ApiClientId, currentBinding.ApiClientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pair.AndroidClientId, currentBinding.AndroidClientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pair.HomeAccountId ?? string.Empty, currentBinding.HomeAccountId ?? string.Empty, StringComparison.Ordinal))
            {
                // Mismatched origin/client/account binding: isolate and do not send to new API
                return null;
            }
        }

        return pair.Credential;
    }

    public async Task SaveCredentialAsync(Guid deviceId, string credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("Credential cannot be empty", nameof(credential));
        }

        var binding = _bindingProvider?.Invoke();

        // Index first: a stored credential must never be unreachable by ClearCredentialsForBindingAsync.
        var indexKey = GetIndexKey(binding);
        if (indexKey is not null)
        {
            var gate = GetIndexGate(indexKey);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var ids = await ReadIndexAsync(indexKey).ConfigureAwait(false);
                if (!ids.Contains(deviceId))
                {
                    ids.Add(deviceId);
                    await WriteIndexAsync(indexKey, ids).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        var key = FormatKey(deviceId, binding);
        var pair = new StoredCredentialPair
        {
            DeviceId = deviceId,
            Credential = credential,
            ApiOrigin = binding?.ApiOrigin,
            ApiClientId = binding?.ApiClientId,
            AndroidClientId = binding?.AndroidClientId,
            HomeAccountId = binding?.HomeAccountId
        };

        var json = JsonSerializer.Serialize(pair);
        await _secureStorage.SetAsync(key, json);
    }

    public Task DeleteCredentialAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return DeleteCredentialAsync(deviceId, _bindingProvider?.Invoke(), cancellationToken);
    }

    /// <summary>
    /// Removes credential material for an explicit binding (call with oldBinding before auth selection changes).
    /// </summary>
    public async Task DeleteCredentialAsync(Guid deviceId, AccountBinding? binding, CancellationToken cancellationToken = default)
    {
        if (binding != null && !string.IsNullOrEmpty(binding.ApiOrigin))
        {
            await _secureStorage.RemoveAsync(FormatKey(deviceId, binding));

            // Also remove pre-signin key variant (same origin/clients, no home account).
            if (!string.IsNullOrEmpty(binding.HomeAccountId))
            {
                var withoutHome = new AccountBinding(binding.ApiOrigin, binding.ApiClientId, binding.AndroidClientId, null);
                await _secureStorage.RemoveAsync(FormatKey(deviceId, withoutHome));
            }
        }
        else
        {
            await _secureStorage.RemoveAsync(FormatKey(deviceId, null));
        }

        // Always clean legacy unbound key if present
        var legacyKey = $"{KeyPrefix}{deviceId:D}";
        try
        {
            await _secureStorage.RemoveAsync(legacyKey);
        }
        catch
        {
            // Ignore
        }

        // Keep the binding index consistent (best effort: the credential itself is already gone).
        var indexKey = GetIndexKey(binding);
        if (indexKey is not null)
        {
            var gate = GetIndexGate(indexKey);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var ids = await ReadIndexAsync(indexKey).ConfigureAwait(false);
                if (ids.Remove(deviceId))
                {
                    await WriteIndexAsync(indexKey, ids).ConfigureAwait(false);
                }
            }
            catch (CredentialIndexCorruptedException)
            {
                // Stale index is harmless here: the credential key was removed above.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Removes every credential that belongs to <paramref name="binding"/> — all server device ids
    /// recorded in the binding index plus the index entry itself (and legacy unbound keys).
    /// Returns the number of server device ids that were purged. Index corruption throws
    /// <see cref="CredentialIndexCorruptedException"/> so callers cannot claim a complete cleanup.
    /// </summary>
    public async Task<int> ClearCredentialsForBindingAsync(
        AccountBinding? binding,
        CancellationToken cancellationToken = default)
    {
        var indexKey = GetIndexKey(binding);
        if (indexKey is null || binding is null)
        {
            // Unbound scope: only the legacy key shape can exist.
            return 0;
        }

        var gate = GetIndexGate(indexKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ids = await ReadIndexAsync(indexKey).ConfigureAwait(false);

            var withoutHome = new AccountBinding(binding.ApiOrigin, binding.ApiClientId, binding.AndroidClientId, null);
            foreach (var deviceId in ids)
            {
                await _secureStorage.RemoveAsync(FormatKey(deviceId, binding)).ConfigureAwait(false);
                await _secureStorage.RemoveAsync(FormatKey(deviceId, withoutHome)).ConfigureAwait(false);
                await _secureStorage.RemoveAsync($"{KeyPrefix}{deviceId:D}").ConfigureAwait(false);
            }

            await _secureStorage.RemoveAsync(indexKey).ConfigureAwait(false);
            return ids.Count;
        }
        finally
        {
            gate.Release();
        }
    }
}
