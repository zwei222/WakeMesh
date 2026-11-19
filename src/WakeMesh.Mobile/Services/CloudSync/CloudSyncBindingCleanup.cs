using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Models;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <param name="Succeeded">True only when every cloud-owned local alarm, manifest mapping and revision was cleared.</param>
/// <param name="Failures">Honest per-step failures; an empty list is required for <c>Succeeded</c>.</param>
public sealed record CloudSyncCleanupResult(
    bool Succeeded,
    IReadOnlyList<AckFailureDto> Failures);

/// <summary>
/// Retires one <see cref="CloudSyncBinding"/> scope: strictly cancels and deletes every cloud alarm
/// that belongs to the binding, clears its manifest mappings and clears the durable applied revision.
/// </summary>
/// <remarks>
/// This is the scope-cleanup entry point that a URL/account change can call for the previous binding;
/// wiring it into the settings/CloudManagement UI is a separate follow-up. Failures are reported, never
/// swallowed: a failed OS cancellation keeps the store entry and the manifest mapping so a retry can
/// still complete the cleanup, and a failed manifest/revision clear reports
/// <c>Succeeded = false</c>.
/// </remarks>
public sealed class CloudSyncBindingCleanup
{
    private readonly CloudSyncBinding _binding;
    private readonly AlarmCoordinator _coordinator;
    private readonly ICloudSyncManifestStore _manifestStore;
    private readonly DurableAppliedRevisionStore? _revisionStore;

    public CloudSyncBindingCleanup(
        CloudSyncBinding binding,
        AlarmCoordinator coordinator,
        ICloudSyncManifestStore manifestStore,
        DurableAppliedRevisionStore? revisionStore = null)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _revisionStore = revisionStore;
    }

    public async Task<CloudSyncCleanupResult> CleanupBindingAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<AckFailureDto>();

        CloudSyncManifest? manifest = null;
        try
        {
            manifest = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown mappings cannot be cleared and the corrupt file is not overwritten with an empty one.
            failures.Add(new AckFailureDto(null, "ManifestLoadFailed", ex.Message));
        }

        // Cloud-owned local alarm ids: manifest mappings plus every deterministic binding-prefixed alarm.
        var targetIds = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        if (manifest is not null)
        {
            foreach (var entry in manifest.Schedules.Values)
            {
                targetIds[entry.LocalAlarmId] = entry.ScheduleId;
            }
        }

        var localAlarms = await _coordinator.GetAllAsync().ConfigureAwait(false);
        foreach (var alarm in localAlarms)
        {
            if (SyncLocalAlarmIdentity.TryParseScheduleId(_binding, alarm.Id, out var scheduleId))
            {
                targetIds.TryAdd(alarm.Id, scheduleId);
            }
        }

        foreach (var (localAlarmId, scheduleId) in targetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _coordinator.CancelThenDeleteAsync(localAlarmId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new AckFailureDto(scheduleId, "AlarmCancelFailed", ex.Message));
            }
        }

        // Partial cleanup: while any OS cancellation failed, the binding still owns live alarms, so the
        // manifest mappings and the applied revision are kept (they are what the retry needs) and the
        // cleanup reports failure instead of a plausible success.
        var alarmsFullyCleaned = failures.All(f => f.Code != "AlarmCancelFailed");

        if (manifest is not null && alarmsFullyCleaned)
        {
            var emptyManifest = new CloudSyncManifest
            {
                ApiBaseUrl = _binding.ApiBaseUrl,
                AccountId = _binding.AccountId,
                ServerDeviceId = _binding.ServerDeviceId,
                RegistrationGeneration = _binding.RegistrationGeneration,
                Schedules = new Dictionary<Guid, SyncScheduleEntry>()
            };

            try
            {
                await _manifestStore.SaveAsync(emptyManifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new AckFailureDto(null, "ManifestClearFailed", ex.Message));
            }
        }

        if (_revisionStore is not null && alarmsFullyCleaned)
        {
            try
            {
                await _revisionStore.ClearAppliedRevisionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new AckFailureDto(null, "RevisionClearFailed", ex.Message));
            }
        }

        return new CloudSyncCleanupResult(failures.Count == 0, failures.ToArray());
    }
}
