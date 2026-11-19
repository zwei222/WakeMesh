using System;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Persistent record for the local device registration binding.
/// Stored in no-backup local storage to prevent cloud backup restoration on a different physical device.
/// </summary>
public sealed record CloudDeviceRegistration
{
    public Guid ServerDeviceId { get; init; }
    public long RegistrationGeneration { get; init; } = 1;
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string DeviceDisplayName { get; init; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public bool RecoveryNeeded { get; init; }
    public string? RecoveryReason { get; init; }

    public CloudDeviceRegistration() { }

    public CloudDeviceRegistration(
        Guid serverDeviceId,
        long registrationGeneration,
        string apiBaseUrl,
        string accountId,
        string deviceDisplayName,
        DateTimeOffset? registeredAt = null,
        bool recoveryNeeded = false,
        string? recoveryReason = null)
    {
        if (serverDeviceId == Guid.Empty)
        {
            throw new ArgumentException("ServerDeviceId cannot be empty.", nameof(serverDeviceId));
        }

        ServerDeviceId = serverDeviceId;
        RegistrationGeneration = registrationGeneration <= 0 ? 1 : registrationGeneration;
        ApiBaseUrl = (apiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        AccountId = (accountId ?? string.Empty).Trim();
        DeviceDisplayName = (deviceDisplayName ?? string.Empty).Trim();
        RegisteredAt = registeredAt ?? DateTimeOffset.UtcNow;
        RecoveryNeeded = recoveryNeeded;
        RecoveryReason = recoveryReason;
    }

    /// <summary>
    /// Checks whether this registration matches the given API origin and account ID.
    /// </summary>
    public bool MatchesScope(string apiBaseUrl, string accountId)
    {
        var normalizedUrl = (apiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var normalizedAccount = (accountId ?? string.Empty).Trim();

        return string.Equals(ApiBaseUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(AccountId, normalizedAccount, StringComparison.Ordinal);
    }
}
