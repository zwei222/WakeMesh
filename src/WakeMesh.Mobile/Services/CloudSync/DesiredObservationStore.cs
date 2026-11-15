using System;
using System.Threading;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// The last desired snapshot this device actually received, together with the outcome of applying it.
/// Nothing is persisted: "反映待ち" is only a fact about the moment the server was read, and showing it
/// from a stale file while offline would be a guess. When nothing is known, <see cref="DesiredObservationStore.Latest"/> is null
/// and the list simply shows the alarms that are applied on this device.
/// </summary>
public sealed record DesiredObservation(
    string BindingKey,
    DesiredSnapshotDto Snapshot,
    DesiredSyncStatus Status,
    DateTimeOffset ObservedAt);

/// <summary>
/// Process-wide, in-memory hand-over from the sync producer (<c>CloudManagementService</c>) to the
/// read-only consumer (the alarm list projection). Deliberately tiny: one immutable reference swapped
/// with <see cref="Volatile"/>, plus a change notification. The consumer must re-read <see cref="Latest"/>
/// on every projection; it never mutates or persists anything here.
/// </summary>
public sealed class DesiredObservationStore
{
    private DesiredObservation? _latest;

    /// <summary>The most recently published observation, or null when nothing is known.</summary>
    public DesiredObservation? Latest => Volatile.Read(ref _latest);

    /// <summary>Raised after <see cref="Publish"/> replaced the value and after a <see cref="Clear"/> that had a value.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Publishes one really-received snapshot for <paramref name="bindingKey"/>. Callers must pass the
    /// binding key of the registration the snapshot was fetched with; a result from an older binding is
    /// never published under a different key.
    /// </summary>
    public void Publish(string bindingKey, DesiredSnapshotDto snapshot, DesiredSyncStatus status, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(bindingKey) || snapshot is null)
        {
            return;
        }

        Volatile.Write(ref _latest, new DesiredObservation(bindingKey, snapshot, status, observedAt));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the observation (sign-out, account / API origin change, registration change).</summary>
    public void Clear()
    {
        var previous = Interlocked.Exchange(ref _latest, null);
        if (previous is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
