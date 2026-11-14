using System;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.Sync.Tests.Unit;

public sealed class CloudSyncBindingTests
{
    [Fact]
    public void Constructor_NormalizesUrl_AndValidatesArguments()
    {
        var binding = new CloudSyncBinding(
            "https://API.Example.com:8443/foo/bar/  ",
            " user-123 ",
            Guid.NewGuid(),
            2);

        Assert.Equal("https://api.example.com:8443/foo/bar", binding.ApiBaseUrl);
        Assert.Equal("user-123", binding.AccountId);
        Assert.Equal(2, binding.RegistrationGeneration);
    }

    [Fact]
    public void Constructor_Throws_OnInvalidArguments()
    {
        var validId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new CloudSyncBinding("", "user", validId));
        Assert.Throws<ArgumentException>(() => new CloudSyncBinding("https://api.example.com", "", validId));
        Assert.Throws<ArgumentException>(() => new CloudSyncBinding("https://api.example.com", "user", Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudSyncBinding("https://api.example.com", "user", validId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudSyncBinding("https://api.example.com", "user", validId, -1));
    }

    [Fact]
    public void StorageKey_IsDeterministic_AndDifferentiatesBindings()
    {
        var devId = Guid.NewGuid();
        var b1 = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var b2 = new CloudSyncBinding("https://api.example.com", "user-1", devId, 1);
        var b3 = new CloudSyncBinding("https://api.example.com", "user-2", devId, 1);
        var b4 = new CloudSyncBinding("https://api.example.com", "user-1", devId, 2);

        Assert.Equal(b1.ToStorageKey(), b2.ToStorageKey());
        Assert.NotEqual(b1.ToStorageKey(), b3.ToStorageKey());
        Assert.NotEqual(b1.ToStorageKey(), b4.ToStorageKey());
        Assert.Matches("^[0-9a-f]{16}$", b1.ToStorageKey());
    }
}
