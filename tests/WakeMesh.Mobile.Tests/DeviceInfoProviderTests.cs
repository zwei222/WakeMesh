using System;
using WakeMesh.Mobile.Services;
using Xunit;

namespace WakeMesh.Mobile.Tests;

public class DeviceInfoProviderTests
{
    [Fact]
    public void DeviceInfoProvider_GeneratesConsistentAnonymousId()
    {
        var provider = new LocalDeviceInfoProvider("fixed-device-id");
        Assert.Equal("fixed-device-id", provider.DeviceId);
        Assert.Equal("この端末 (ローカル)", provider.DeviceDisplayName);
    }

    [Fact]
    public void DeviceInfoProvider_WithoutSpecifiedId_GeneratesValidGuidId()
    {
        var provider = new LocalDeviceInfoProvider();
        Assert.False(string.IsNullOrWhiteSpace(provider.DeviceId));
        Assert.True(Guid.TryParse(provider.DeviceId, out _));
        Assert.Equal("この端末 (ローカル)", provider.DeviceDisplayName);
    }
}
