using System;
using System.Collections.Generic;
using System.Globalization;
using WakeMesh.Mobile.Services.CloudManagement;

namespace WakeMesh.Mobile.Services.Push;

public static class PushPayloadValidator
{
    public const string SettingsChangedType = "settingsChanged";

    /// <summary>
    /// Validates raw FCM data payload against current cloud registration binding.
    /// Strictly ignores messages for other devices or scope mismatches.
    /// Payload contents, tokens, and credentials are never logged.
    /// </summary>
    public static PushPayloadValidationResult Validate(
        IReadOnlyDictionary<string, string>? data,
        CloudDeviceRegistration? registration,
        string currentApiBaseUrl,
        string? currentAccountId)
    {
        if (data == null || data.Count == 0)
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.MissingData, "MissingData");
        }

        if (!data.TryGetValue("type", out var type) ||
            !string.Equals(type, SettingsChangedType, StringComparison.Ordinal))
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.InvalidType, "InvalidType");
        }

        if (!data.TryGetValue("deviceId", out var deviceIdStr) ||
            !Guid.TryParse(deviceIdStr, out var deviceId) ||
            deviceId == Guid.Empty)
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.InvalidDeviceId, "InvalidDeviceId");
        }

        if (!data.TryGetValue("revision", out var revisionStr) ||
            !long.TryParse(revisionStr, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.InvalidRevision, "InvalidRevision");
        }

        if (registration == null || registration.RecoveryNeeded)
        {
            return PushPayloadValidationResult.Failed(
                PushValidationStatus.NotRegisteredOrRecoveryNeeded,
                "NotRegisteredOrRecoveryNeeded");
        }

        if (registration.ServerDeviceId != deviceId)
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.DeviceMismatch, "DeviceMismatch");
        }

        if (!registration.MatchesScope(currentApiBaseUrl, currentAccountId ?? string.Empty))
        {
            return PushPayloadValidationResult.Failed(PushValidationStatus.ScopeMismatch, "ScopeMismatch");
        }

        return PushPayloadValidationResult.Valid(new PushMessagePayload(type, deviceId, revision));
    }
}
