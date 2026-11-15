using System;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Production reader for "what has already been applied on this device". It rebuilds the current binding
/// from the persisted registration (readable offline) — the same fields the sync path uses — and then reads
/// that binding's manifest read-only. It never saves a manifest, never reserves an OS alarm and never
/// changes the registration; a corrupt or mismatched manifest is reported as unreadable instead of being
/// treated as empty, so previously applied rows are still listed (just without a group link).
/// </summary>
public sealed class GroupAlarmSource : IGroupAlarmSource
{
    private readonly ICloudDeviceRegistrationStore _registrationStore;
    private readonly string? _customStorageDir;

    public GroupAlarmSource(ICloudDeviceRegistrationStore registrationStore, string? customStorageDir = null)
    {
        _registrationStore = registrationStore ?? throw new ArgumentNullException(nameof(registrationStore));
        _customStorageDir = customStorageDir;
    }

    public async Task<GroupAlarmSourceResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        CloudDeviceRegistration? registration;
        try
        {
            registration = await _registrationStore.LoadRegistrationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An unreadable registration is "unknown", not "no cloud alarms": the stored rows stay listed.
            return GroupAlarmSourceResult.None;
        }

        if (registration is null ||
            registration.ServerDeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(registration.ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(registration.AccountId))
        {
            return GroupAlarmSourceResult.None;
        }

        CloudSyncBinding binding;
        try
        {
            binding = new CloudSyncBinding(
                registration.ApiBaseUrl,
                registration.AccountId,
                registration.ServerDeviceId,
                registration.RegistrationGeneration);
        }
        catch (ArgumentException)
        {
            return GroupAlarmSourceResult.None;
        }

        var baseDir = CloudSyncStoragePaths.ResolveBaseDirectory(_customStorageDir);
        var manifestStore = new FileCloudSyncManifestStore(binding, CloudSyncStoragePaths.Manifest(baseDir, binding));

        try
        {
            var manifest = await manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new GroupAlarmSourceResult(binding, manifest, ManifestUnreadable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CorruptedSyncManifestException)
        {
            return new GroupAlarmSourceResult(binding, Manifest: null, ManifestUnreadable: true);
        }
        catch
        {
            // IO failure: keep the binding so prefix-owned rows still show, but flag the missing mapping.
            return new GroupAlarmSourceResult(binding, Manifest: null, ManifestUnreadable: true);
        }
    }
}
