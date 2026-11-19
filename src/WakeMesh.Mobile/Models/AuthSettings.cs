using System;
using System.Text.Json.Serialization;

namespace WakeMesh.Mobile.Models;

public sealed class AuthSettings
{
    private string? _apiBaseUrl;
    private string? _androidClientId;
    private string? _apiClientId;

    public string? ApiBaseUrl
    {
        get => _apiBaseUrl;
        set => _apiBaseUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string? AndroidClientId
    {
        get => _androidClientId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _androidClientId = null;
            }
            else if (Guid.TryParse(value.Trim(), out var guid))
            {
                _androidClientId = guid.ToString("d");
            }
            else
            {
                _androidClientId = value.Trim();
            }
        }
    }

    public string? ApiClientId
    {
        get => _apiClientId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _apiClientId = null;
            }
            else if (Guid.TryParse(value.Trim(), out var guid))
            {
                _apiClientId = guid.ToString("d");
            }
            else
            {
                _apiClientId = value.Trim();
            }
        }
    }

    // CustomScope is deprecated and removed per security review. Scope is fixed strictly to api://{ApiClientId}/access_as_user.
    // Backward compatibility: ignore CustomScope if present in old JSON payloads during deserialization.
    [JsonExtensionData]
    public System.Collections.Generic.Dictionary<string, object>? ExtensionData { get; set; }

    public string EffectiveScope
    {
        get
        {
            var apiId = AccountBinding.NormalizeGuid(ApiClientId);
            if (string.IsNullOrWhiteSpace(apiId) || AccountBinding.IsZeroOrEmptyGuid(apiId))
            {
                return string.Empty;
            }

            return $"api://{apiId}/access_as_user";
        }
    }

    public AuthSettingsValidationResult Validate()
    {
        var urlEmpty = string.IsNullOrWhiteSpace(ApiBaseUrl);
        var androidEmpty = string.IsNullOrWhiteSpace(AndroidClientId);
        var apiEmpty = string.IsNullOrWhiteSpace(ApiClientId);

        // All fields empty: valid unconfigured state
        if (urlEmpty && androidEmpty && apiEmpty)
        {
            return new AuthSettingsValidationResult(true, false, null);
        }

        // Format validations first
        if (!urlEmpty)
        {
            if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return new AuthSettingsValidationResult(false, true, "API Base URLは有効な https:// URLである必要があります。");
            }
        }

        if (!androidEmpty)
        {
            if (!Guid.TryParse(AndroidClientId, out _))
            {
                return new AuthSettingsValidationResult(false, true, "Android Client IDは有効なGUID形式である必要があります。");
            }
        }

        if (!apiEmpty)
        {
            if (!Guid.TryParse(ApiClientId, out _))
            {
                return new AuthSettingsValidationResult(false, true, "API Client IDは有効なGUID形式である必要があります。");
            }
        }

        // Partially filled fields
        if (urlEmpty || androidEmpty || apiEmpty)
        {
            return new AuthSettingsValidationResult(false, false, "API Base URL、Android Client ID、API Client IDのすべての設定が必要です。");
        }

        // Zero GUIDs are validly treated as 'unconfigured' placeholder states (Signin refused, no API calls)
        var androidIsZero = AccountBinding.IsZeroOrEmptyGuid(AndroidClientId);
        var apiIsZero = AccountBinding.IsZeroOrEmptyGuid(ApiClientId);
        if (androidIsZero || apiIsZero)
        {
            return new AuthSettingsValidationResult(true, false, null);
        }

        return new AuthSettingsValidationResult(true, true, null);
    }

    public bool MatchesCompileTimeClientId(string compileTimeClientId)
    {
        var runtimeId = AccountBinding.NormalizeGuid(AndroidClientId);
        var compileId = AccountBinding.NormalizeGuid(compileTimeClientId);

        if (string.IsNullOrWhiteSpace(runtimeId) || string.IsNullOrWhiteSpace(compileId))
        {
            return false;
        }

        if (AccountBinding.IsZeroOrEmptyGuid(runtimeId) || AccountBinding.IsZeroOrEmptyGuid(compileId))
        {
            // Zero GUID is unconfigured placeholder; cannot match for login
            return false;
        }

        return string.Equals(runtimeId, compileId, StringComparison.Ordinal);
    }

    public AccountBinding ToAccountBinding(string? homeAccountId = null)
    {
        return new AccountBinding(
            ApiBaseUrl ?? string.Empty,
            ApiClientId ?? string.Empty,
            AndroidClientId ?? string.Empty,
            homeAccountId);
    }
}

public sealed record AuthSettingsValidationResult(bool IsValid, bool IsConfigured, string? ErrorMessage);
