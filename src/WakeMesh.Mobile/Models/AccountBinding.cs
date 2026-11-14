using System;

namespace WakeMesh.Mobile.Models;

/// <summary>
/// Structured account and device credential binding.
/// Binds normalized HTTPS API origin, API Client ID, Android Client ID, and MSAL HomeAccountId.
/// </summary>
public sealed record AccountBinding
{
    public string ApiOrigin { get; init; } = string.Empty;
    public string ApiClientId { get; init; } = string.Empty;
    public string AndroidClientId { get; init; } = string.Empty;
    public string? HomeAccountId { get; init; }

    public AccountBinding() { }

    public AccountBinding(string apiOrigin, string apiClientId, string androidClientId, string? homeAccountId = null)
    {
        ApiOrigin = NormalizeApiOrigin(apiOrigin);
        ApiClientId = NormalizeGuid(apiClientId) ?? string.Empty;
        AndroidClientId = NormalizeGuid(androidClientId) ?? string.Empty;
        HomeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId.Trim();
    }

    /// <summary>
    /// Normalizes an API base URL into a canonical HTTPS origin (e.g. "https://api.example.com:8443").
    /// Rejects non-HTTPS schemes.
    /// </summary>
    public static string NormalizeApiOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return string.Empty;
        }

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{portPart}";
    }

    /// <summary>
    /// Normalizes a GUID string into canonical lowercase 'd' format (e.g. 00000000-0000-0000-0000-000000000000).
    /// </summary>
    public static string? NormalizeGuid(string? guidString)
    {
        if (string.IsNullOrWhiteSpace(guidString))
        {
            return null;
        }

        return Guid.TryParse(guidString.Trim(), out var g) ? g.ToString("d") : null;
    }

    /// <summary>
    /// Checks if a GUID string represents the unconfigured zero GUID.
    /// </summary>
    public static bool IsZeroOrEmptyGuid(string? guidString)
    {
        if (string.IsNullOrWhiteSpace(guidString))
        {
            return true;
        }

        return Guid.TryParse(guidString.Trim(), out var g) && g == Guid.Empty;
    }

    /// <summary>
    /// Deterministic storage key suffix representing this binding scope.
    /// </summary>
    public string GetBindingKey()
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var raw = $"{ApiOrigin}|{ApiClientId}|{AndroidClientId}|{HomeAccountId ?? string.Empty}";
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
