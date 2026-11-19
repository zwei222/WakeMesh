using System;
using System.Collections.Generic;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;
using Xunit;

namespace WakeMesh.Mobile.Push.Tests;

public sealed class PushPayloadValidatorTests
{
    private const string ApiUrl = "https://api.example.com";
    private const string AccountId = "user-123";
    private readonly Guid _myDeviceId = Guid.NewGuid();

    private CloudDeviceRegistration CreateValidRegistration(Guid deviceId) =>
        new(
            serverDeviceId: deviceId,
            registrationGeneration: 1,
            apiBaseUrl: ApiUrl,
            accountId: AccountId,
            deviceDisplayName: "Test Device",
            registeredAt: DateTimeOffset.UtcNow,
            recoveryNeeded: false,
            recoveryReason: null);

    [Fact]
    public void Validate_ValidSettingsChangedPayload_ReturnsValidResult()
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _myDeviceId.ToString("D"),
            ["revision"] = "42"
        };

        var result = PushPayloadValidator.Validate(data, reg, ApiUrl, AccountId);

        Assert.True(result.IsValid);
        Assert.Equal(PushValidationStatus.Valid, result.Status);
        Assert.NotNull(result.Payload);
        Assert.Equal("settingsChanged", result.Payload.Type);
        Assert.Equal(_myDeviceId, result.Payload.DeviceId);
        Assert.Equal(42L, result.Payload.Revision);
    }

    [Fact]
    public void Validate_NullOrEmptyData_ReturnsMissingData()
    {
        var reg = CreateValidRegistration(_myDeviceId);

        var nullResult = PushPayloadValidator.Validate(null, reg, ApiUrl, AccountId);
        Assert.False(nullResult.IsValid);
        Assert.Equal(PushValidationStatus.MissingData, nullResult.Status);

        var emptyResult = PushPayloadValidator.Validate(new Dictionary<string, string>(), reg, ApiUrl, AccountId);
        Assert.False(emptyResult.IsValid);
        Assert.Equal(PushValidationStatus.MissingData, emptyResult.Status);
    }

    [Theory]
    [InlineData("alarmFired")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Validate_InvalidType_ReturnsInvalidType(string invalidType)
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var data = new Dictionary<string, string>
        {
            ["type"] = invalidType,
            ["deviceId"] = _myDeviceId.ToString("D"),
            ["revision"] = "1"
        };

        var result = PushPayloadValidator.Validate(data, reg, ApiUrl, AccountId);

        Assert.False(result.IsValid);
        Assert.Equal(PushValidationStatus.InvalidType, result.Status);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void Validate_InvalidDeviceId_ReturnsInvalidDeviceId(string invalidDeviceId)
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = invalidDeviceId,
            ["revision"] = "1"
        };

        var result = PushPayloadValidator.Validate(data, reg, ApiUrl, AccountId);

        Assert.False(result.IsValid);
        Assert.Equal(PushValidationStatus.InvalidDeviceId, result.Status);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void Validate_InvalidRevision_ReturnsInvalidRevision(string invalidRevision)
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _myDeviceId.ToString("D"),
            ["revision"] = invalidRevision
        };

        var result = PushPayloadValidator.Validate(data, reg, ApiUrl, AccountId);

        Assert.False(result.IsValid);
        Assert.Equal(PushValidationStatus.InvalidRevision, result.Status);
    }

    [Fact]
    public void Validate_DeviceMismatch_ReturnsDeviceMismatch()
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var otherDevice = Guid.NewGuid();
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = otherDevice.ToString("D"),
            ["revision"] = "1"
        };

        var result = PushPayloadValidator.Validate(data, reg, ApiUrl, AccountId);

        Assert.False(result.IsValid);
        Assert.Equal(PushValidationStatus.DeviceMismatch, result.Status);
    }

    [Fact]
    public void Validate_NullOrRecoveryNeededRegistration_ReturnsNotRegisteredOrRecoveryNeeded()
    {
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _myDeviceId.ToString("D"),
            ["revision"] = "1"
        };

        var nullResult = PushPayloadValidator.Validate(data, null, ApiUrl, AccountId);
        Assert.False(nullResult.IsValid);
        Assert.Equal(PushValidationStatus.NotRegisteredOrRecoveryNeeded, nullResult.Status);

        var recoveryReg = new CloudDeviceRegistration(
            serverDeviceId: _myDeviceId,
            registrationGeneration: 1,
            apiBaseUrl: ApiUrl,
            accountId: AccountId,
            deviceDisplayName: "Test",
            recoveryNeeded: true,
            recoveryReason: "reinstall");

        var recoveryResult = PushPayloadValidator.Validate(data, recoveryReg, ApiUrl, AccountId);
        Assert.False(recoveryResult.IsValid);
        Assert.Equal(PushValidationStatus.NotRegisteredOrRecoveryNeeded, recoveryResult.Status);
    }

    [Fact]
    public void Validate_ScopeMismatch_ReturnsScopeMismatch()
    {
        var reg = CreateValidRegistration(_myDeviceId);
        var data = new Dictionary<string, string>
        {
            ["type"] = "settingsChanged",
            ["deviceId"] = _myDeviceId.ToString("D"),
            ["revision"] = "1"
        };

        // URL mismatch
        var urlResult = PushPayloadValidator.Validate(data, reg, "https://different.example.com", AccountId);
        Assert.False(urlResult.IsValid);
        Assert.Equal(PushValidationStatus.ScopeMismatch, urlResult.Status);

        // Account mismatch
        var accountResult = PushPayloadValidator.Validate(data, reg, ApiUrl, "different-account");
        Assert.False(accountResult.IsValid);
        Assert.Equal(PushValidationStatus.ScopeMismatch, accountResult.Status);
    }
}
