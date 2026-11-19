using System;
using System.Security.Cryptography;
using System.Text;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Immutable binding parameters identifying the cloud synchronization context.
/// Binds API base URL, owner account, server device ID, and registration generation.
/// </summary>
public sealed record CloudSyncBinding
{
    public string ApiBaseUrl { get; }
    public string AccountId { get; }
    public Guid ServerDeviceId { get; }
    public long RegistrationGeneration { get; }

    public CloudSyncBinding(
        string apiBaseUrl,
        string accountId,
        Guid serverDeviceId,
        long registrationGeneration = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (serverDeviceId == Guid.Empty)
        {
            throw new ArgumentException("ServerDeviceId cannot be empty.", nameof(serverDeviceId));
        }

        if (registrationGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(registrationGeneration), "Generation must be positive.");
        }

        ApiBaseUrl = NormalizeUrl(apiBaseUrl);
        AccountId = accountId.Trim();
        ServerDeviceId = serverDeviceId;
        RegistrationGeneration = registrationGeneration;
    }

    public static string NormalizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var uri = new Uri(url.Trim(), UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>
    /// Generates a deterministic safe key for isolating local storage files.
    /// </summary>
    public string ToStorageKey()
    {
        var raw = $"{ApiBaseUrl.ToLowerInvariant()}|{AccountId}|{ServerDeviceId:N}|{RegistrationGeneration}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
