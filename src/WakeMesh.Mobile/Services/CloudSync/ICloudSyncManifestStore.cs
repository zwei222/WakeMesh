using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.CloudSync;

public interface ICloudSyncManifestStore
{
    Task<CloudSyncManifest> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CloudSyncManifest manifest, CancellationToken cancellationToken = default);
}
