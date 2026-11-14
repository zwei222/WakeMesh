using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public interface IAuthSettingsStore
{
    Task<AuthSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default);
}
