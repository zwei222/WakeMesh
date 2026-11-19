using System;
using System.Collections.Generic;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class NoSecretLoggingTests
{
    [Fact]
    public void PushMessagePayload_ToString_DoesNotContainCredentials()
    {
        var deviceId = Guid.NewGuid();
        var payload = new PushMessagePayload("settingsChanged", deviceId, 42);

        var text = payload.ToString();

        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PushPayloadValidator_RejectionReasons_NeverContainPayloadValues()
    {
        var sensitiveData = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = Guid.NewGuid().ToString(),
            ["revision"] = "99"
        };

        var reg = new CloudDeviceRegistration(
            serverDeviceId: Guid.NewGuid(), // Mismatched
            registrationGeneration: 1,
            apiBaseUrl: "https://api.example.com",
            accountId: "account-1",
            deviceDisplayName: "Device");

        var result = PushPayloadValidator.Validate(sensitiveData, reg, "https://api.example.com", "account-1");

        Assert.False(result.IsValid);
        Assert.Equal("DeviceMismatch", result.ErrorReason);
        Assert.DoesNotContain("99", result.ErrorReason);
        Assert.DoesNotContain(reg.ServerDeviceId.ToString(), result.ErrorReason);
    }
}
