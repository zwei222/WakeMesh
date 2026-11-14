namespace WakeMesh.Mobile.Services;

public interface IDeviceInfoProvider
{
    string DeviceId { get; }
    string DeviceDisplayName { get; }
}
