using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Store for persisting local device registration state in no-backup storage.
/// </summary>
public interface ICloudDeviceRegistrationStore
{
    Task<CloudDeviceRegistration?> LoadRegistrationAsync(CancellationToken cancellationToken = default);
    Task SaveRegistrationAsync(CloudDeviceRegistration registration, CancellationToken cancellationToken = default);
    Task ClearRegistrationAsync(CancellationToken cancellationToken = default);
}
