namespace WakeMesh.Client;

/// <summary>
/// Durable per-device high-water mark for successfully applied desired revisions.
/// Production MAUI must supply a restart-surviving implementation; in-memory is test-only.
/// </summary>
/// <remarks>
/// <para>
/// Contract order for a successful apply cycle:
/// </para>
/// <list type="number">
/// <item><description>OS / local schedule apply completes for revision R (and device id binding checks pass).</description></item>
/// <item><description><see cref="CommitAppliedRevisionAsync"/> persists R (must not roll back on process recreate once returned).</description></item>
/// <item><description>Only then may the client send a success ACK for R.</description></item>
/// </list>
/// <para>
/// Commit must be monotonic: implementations must ignore or no-op commits where
/// <c>revision</c> is less than the stored value for that <c>deviceId</c>.
/// A failed process after apply but before commit may re-apply the same or newer revision on restart;
/// a failed process after commit but before ACK may re-ACK the same revision with a <em>new</em> receipt
/// without lowering the store and without re-running the OS applier.
/// Only revisions <b>strictly less than</b> the committed high-water are stale (no apply, no ACK).
/// Equal revision after commit is the recovery/re-ACK path, not stale.
/// </para>
/// <para>
/// <see cref="GetAppliedRevisionAsync"/> returns <c>null</c> when nothing has been committed yet so that
/// desired revision <c>0</c> (valid empty snapshot on a new device) can still be applied once.
/// Do not treat “missing” as zero if that would collapse never-applied and applied-0.
/// </para>
/// <para>
/// Future Mobile durable keys should bind normalized API base URL + account binding + serverDeviceId
/// + registration generation. A DB restore that surfaces a lower server generation must not silently
/// reset or ACK under a stale local high-water — surface recovery instead of auto-downgrade.
/// </para>
/// </remarks>
public interface IAppliedRevisionStore
{
    /// <returns>Last committed applied revision for the device, or <c>null</c> if none committed yet.</returns>
    Task<long?> GetAppliedRevisionAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist that revision <paramref name="revision"/> was fully applied for <paramref name="deviceId"/>.
    /// Called only after local apply success and before success ACK. Must survive process restart.
    /// Revision may be <c>0</c> (empty cancel snapshot).
    /// </summary>
    Task CommitAppliedRevisionAsync(Guid deviceId, long revision, CancellationToken cancellationToken = default);
}
