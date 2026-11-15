using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// What the reader found on disk. <see cref="Binding"/> is null when no usable registration exists (never
/// signed in, or the registration file is unreadable). <see cref="Manifest"/> is null when it is missing
/// or unreadable; <see cref="ManifestUnreadable"/> distinguishes "corrupt / mismatched" from "absent".
/// </summary>
public sealed record GroupAlarmSourceResult(
    CloudSyncBinding? Binding,
    CloudSyncManifest? Manifest,
    bool ManifestUnreadable)
{
    public static readonly GroupAlarmSourceResult None = new(null, null, false);
}

/// <summary>
/// Read-only access to what the applier has already applied on this device: the persisted registration
/// (which yields the current binding, readable offline) plus that binding's manifest. Implementations
/// must never write, never reserve an OS alarm and never extend a manifest.
/// </summary>
public interface IGroupAlarmSource
{
    Task<GroupAlarmSourceResult> LoadAsync(CancellationToken cancellationToken = default);
}
