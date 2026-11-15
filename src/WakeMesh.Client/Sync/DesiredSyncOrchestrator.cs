using System.Collections.Concurrent;
using System.Text.Json;
using WakeMesh.Client.Models;

namespace WakeMesh.Client.Sync;

/// <summary>
/// Applies a desired snapshot locally: durable store + OS alarm register/cancel.
/// Real Android AlarmManager applier is intentionally NOT implemented here — MAUI package later.
/// </summary>
public interface IDesiredApplier
{
    /// <summary>
    /// Persist the full desired set and complete OS registration/cancellation for this device's schedules only.
    /// Must not touch alarms owned by other apps. Partial item failure should return Succeeded=false with failures.
    /// </summary>
    Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto snapshot, CancellationToken cancellationToken = default);
}

/// <summary>
/// T135: optional capability of an applier that stores a payload-format marker locally. When an incoming
/// snapshot carries exactly the revision already committed, the orchestrator normally re-ACKs without
/// touching the OS. An applier whose local payload predates the current item format can ask for one
/// re-apply instead, so an upgraded client picks up fields the previous build never wrote.
/// </summary>
public interface IEqualRevisionReapplyProbe
{
    /// <summary>
    /// True when the applied payload must be rebuilt at the equal revision. Must be read-only: the
    /// orchestrator calls it before deciding whether to run <see cref="IDesiredApplier.ApplyAsync"/>.
    /// </summary>
    Task<bool> RequiresReapplyAtEqualRevisionAsync(CancellationToken cancellationToken = default);
}

/// <param name="Succeeded">True only when every item/tombstone was applied.</param>
/// <param name="Failures">Per-schedule failures when Succeeded is false.</param>
/// <param name="Completed">False if apply did not finish (cancel/incomplete) — orchestrator must NOT send ACK.</param>
public sealed record DesiredApplyResult(
    bool Succeeded,
    bool Completed,
    IReadOnlyList<AckFailureDto> Failures);

/// <summary>Outcome of one read-only desired sync cycle.</summary>
public enum DesiredSyncStatus
{
    /// <summary>Desired fetched, applied fully, success ACK accepted by server (or equal-rev recovery re-ACK).</summary>
    AppliedAcknowledged = 0,
    /// <summary>Apply finished with failures; failed ACK was sent.</summary>
    ApplyFailedAcknowledged = 1,
    /// <summary>Apply did not complete (cancel/incomplete/applier fault); no ACK sent.</summary>
    ApplyIncompleteNoAck = 2,
    /// <summary>Caller canceled before or during the cycle; no applied ACK.</summary>
    CanceledNoAck = 3,
    /// <summary>Could not fetch desired or post ACK, or desired schema invalid.</summary>
    TransportFailed = 4,
    /// <summary>
    /// Snapshot revision is strictly behind the durable applied high-water mark.
    /// No OS apply and no ACK for a lower revision.
    /// </summary>
    StaleRevisionSkipped = 5,
}

public sealed record DesiredSyncOutcome(
    DesiredSyncStatus Status,
    DesiredSnapshotDto? Snapshot,
    AckResponse? Ack,
    ApiErrorKind? ErrorKind,
    string? ErrorCode);

/// <summary>
/// Minimal read-only sync: GET desired → local applier (persist + OS) → commit applied revision → ACK.
/// Per-device gate is process-wide so multiple orchestrator instances serialize on the same device id.
/// </summary>
/// <remarks>
/// Apply/commit ordering: OS apply success → <see cref="IAppliedRevisionStore.CommitAppliedRevisionAsync"/> → success ACK.
/// The store is required (no implicit in-memory fallback) and must be durable across process restarts.
/// Snapshots that fail desired schema validation are <see cref="ApiErrorKind.InvalidResponse"/> (no apply/commit/ACK).
/// Equal committed revision: skip applier, success-ACK with the new receipt (ACK-loss recovery) —
/// unless the applier implements <see cref="IEqualRevisionReapplyProbe"/> and asks for one re-apply.
/// Strictly lower revision: <see cref="DesiredSyncStatus.StaleRevisionSkipped"/>.
/// </remarks>
public sealed class DesiredSyncOrchestrator
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DeviceGates = new();

    private readonly WakeMeshApiClient _api;
    private readonly IDesiredApplier _applier;
    private readonly IAppliedRevisionStore _appliedRevisions;

    public DesiredSyncOrchestrator(
        WakeMeshApiClient api,
        IDesiredApplier applier,
        IAppliedRevisionStore appliedRevisionStore)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _appliedRevisions = appliedRevisionStore ?? throw new ArgumentNullException(nameof(appliedRevisionStore));
    }

    public async Task<DesiredSyncOutcome> SyncDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "canceled");
        }

        var gate = DeviceGates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "canceled");
        }

        try
        {
            return await SyncDeviceLockedAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, ApiErrorKind.Canceled, "canceled");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DesiredSyncOutcome> SyncDeviceLockedAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var desired = await _api.GetDesiredAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (!desired.IsSuccess || desired.Value is null)
        {
            if (desired.Kind == ApiErrorKind.Canceled)
            {
                return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, null, null, desired.Kind, desired.ErrorCode);
            }

            return new DesiredSyncOutcome(
                DesiredSyncStatus.TransportFailed,
                null,
                null,
                desired.Kind,
                desired.ErrorCode ?? desired.Kind.ToString());
        }

        var snapshot = desired.Value;
        // Defense in depth — GetDesiredAsync already validates; keep orchestrator fail-closed.
        if (!DesiredSnapshotValidator.TryValidate(snapshot, deviceId, out var schemaCode))
        {
            return new DesiredSyncOutcome(
                DesiredSyncStatus.TransportFailed,
                null,
                null,
                ApiErrorKind.InvalidResponse,
                schemaCode);
        }

        long? committed;
        try
        {
            committed = await _appliedRevisions.GetAppliedRevisionAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ApiErrorKind.Canceled, "canceled");
        }
        catch (Exception)
        {
            return new DesiredSyncOutcome(
                DesiredSyncStatus.TransportFailed,
                snapshot,
                null,
                ApiErrorKind.CredentialStoreFailed,
                "appliedRevisionStoreFailed");
        }

        if (committed is long highWater && snapshot.Revision < highWater)
        {
            // Strictly lower only — equal is recovery re-ACK, not stale.
            return new DesiredSyncOutcome(DesiredSyncStatus.StaleRevisionSkipped, snapshot, null, null, "staleRevision");
        }

        if (committed is long applied && snapshot.Revision == applied)
        {
            // Already applied: do not re-run OS applier; success-ACK with the fresh receipt.
            // T135: an applier that can prove its local payload predates the current item format
            // (for example, the build that never stored ExcludedDates) gets exactly one re-apply.
            if (_applier is not IEqualRevisionReapplyProbe probe)
            {
                return await PostSuccessAckAsync(deviceId, snapshot, cancellationToken).ConfigureAwait(false);
            }

            bool requiresReapply;
            try
            {
                requiresReapply = await probe.RequiresReapplyAtEqualRevisionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ApiErrorKind.Canceled, "canceled");
            }
            catch (Exception)
            {
                // The marker could not be read, so the local payload may be stale: fail closed with a
                // fixed classification. No store/OS mutation, no success ACK — the next cycle retries.
                return new DesiredSyncOutcome(
                    DesiredSyncStatus.ApplyIncompleteNoAck,
                    snapshot,
                    null,
                    ApiErrorKind.Unexpected,
                    "equalRevisionProbeFailed");
            }

            if (!requiresReapply)
            {
                return await PostSuccessAckAsync(deviceId, snapshot, cancellationToken).ConfigureAwait(false);
            }

            // Fall through to the normal apply path: apply → commit the same revision → success ACK.
            // The commit store only refuses to move the mark backwards, so an equal commit is a no-op.
        }

        DesiredApplyResult apply;
        try
        {
            apply = await _applier.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ApiErrorKind.Canceled, "canceled");
        }
        catch (Exception)
        {
            // Fixed classification — never surface applier exception text.
            return new DesiredSyncOutcome(
                DesiredSyncStatus.ApplyIncompleteNoAck,
                snapshot,
                null,
                ApiErrorKind.Unexpected,
                "applierFailed");
        }

        if (!apply.Completed || cancellationToken.IsCancellationRequested)
        {
            return new DesiredSyncOutcome(DesiredSyncStatus.ApplyIncompleteNoAck, snapshot, null, ApiErrorKind.Canceled, "incomplete");
        }

        // Succeeded + Failures must not be treated as a clean apply / applied commit.
        var cleanSuccess = apply.Succeeded && (apply.Failures is null || apply.Failures.Count == 0);

        if (cleanSuccess)
        {
            try
            {
                // Commit durable high-water BEFORE success ACK so process death cannot ACK without store advance,
                // and so a later process cannot apply a lower revision after this commit.
                await _appliedRevisions.CommitAppliedRevisionAsync(deviceId, snapshot.Revision, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ApiErrorKind.Canceled, "canceled");
            }
            catch (Exception)
            {
                return new DesiredSyncOutcome(
                    DesiredSyncStatus.TransportFailed,
                    snapshot,
                    null,
                    ApiErrorKind.CredentialStoreFailed,
                    "appliedRevisionCommitFailed");
            }

            return await PostSuccessAckAsync(deviceId, snapshot, cancellationToken).ConfigureAwait(false);
        }

        var ackRequest = new AckRequest(
            snapshot.ReceiptId,
            snapshot.Revision,
            Result: "failure",
            Failures: apply.Failures is { Count: > 0 }
                ? apply.Failures
                : new[] { new AckFailureDto(null, "applyFailed", "applyFailed") },
            ClientReportedAt: null);

        var ack = await _api.PostAckAsync(deviceId, ackRequest, cancellationToken).ConfigureAwait(false);
        if (!ack.IsSuccess)
        {
            if (ack.Kind == ApiErrorKind.Canceled)
            {
                return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ack.Kind, ack.ErrorCode);
            }

            return new DesiredSyncOutcome(DesiredSyncStatus.TransportFailed, snapshot, null, ack.Kind, ack.ErrorCode);
        }

        return new DesiredSyncOutcome(DesiredSyncStatus.ApplyFailedAcknowledged, snapshot, ack.Value, null, null);
    }

    private async Task<DesiredSyncOutcome> PostSuccessAckAsync(
        Guid deviceId,
        DesiredSnapshotDto snapshot,
        CancellationToken cancellationToken)
    {
        var ackRequest = new AckRequest(
            snapshot.ReceiptId,
            snapshot.Revision,
            Result: "success",
            Failures: Array.Empty<AckFailureDto>(),
            ClientReportedAt: null);

        var ack = await _api.PostAckAsync(deviceId, ackRequest, cancellationToken).ConfigureAwait(false);
        if (!ack.IsSuccess)
        {
            if (ack.Kind == ApiErrorKind.Canceled)
            {
                return new DesiredSyncOutcome(DesiredSyncStatus.CanceledNoAck, snapshot, null, ack.Kind, ack.ErrorCode);
            }

            return new DesiredSyncOutcome(DesiredSyncStatus.TransportFailed, snapshot, null, ack.Kind, ack.ErrorCode);
        }

        return new DesiredSyncOutcome(DesiredSyncStatus.AppliedAcknowledged, snapshot, ack.Value, null, null);
    }
}

/// <summary>Desired snapshot wire-schema checks shared by API client and orchestrator.</summary>
internal static class DesiredSnapshotValidator
{
    public static bool TryValidate(DesiredSnapshotDto? snapshot, Guid expectedDeviceId, out string errorCode)
    {
        if (snapshot is null)
        {
            errorCode = "nullBody";
            return false;
        }

        if (snapshot.Revision < 0)
        {
            errorCode = "negativeRevision";
            return false;
        }

        if (snapshot.DeviceId == Guid.Empty)
        {
            errorCode = "emptyDeviceId";
            return false;
        }

        if (snapshot.DeviceId != expectedDeviceId)
        {
            errorCode = "deviceIdMismatch";
            return false;
        }

        if (snapshot.ReceiptId == Guid.Empty)
        {
            errorCode = "emptyReceiptId";
            return false;
        }

        if (snapshot.Items is null)
        {
            errorCode = "nullItems";
            return false;
        }

        if (snapshot.Tombstones is null)
        {
            errorCode = "nullTombstones";
            return false;
        }

        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            if (snapshot.Items[i] is null)
            {
                errorCode = "nullItemElement";
                return false;
            }
        }

        for (var i = 0; i < snapshot.Tombstones.Count; i++)
        {
            if (snapshot.Tombstones[i] is null)
            {
                errorCode = "nullTombstoneElement";
                return false;
            }
        }

        errorCode = "";
        return true;
    }

    /// <summary>
    /// Validate raw JSON before/after deserialize when STJ would otherwise materialize nulls poorly.
    /// Returns false with InvalidResponse code when schema is illegal.
    /// </summary>
    public static bool TryValidateRaw(string json, Guid expectedDeviceId, out DesiredSnapshotDto? snapshot, out string errorCode)
    {
        snapshot = null;
        errorCode = "invalidJson";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalidJson";
                return false;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("deviceId", out var deviceEl) || deviceEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(deviceEl.GetString(), out var deviceId))
            {
                errorCode = "emptyDeviceId";
                return false;
            }

            if (!root.TryGetProperty("revision", out var revEl) || revEl.ValueKind != JsonValueKind.Number
                || !revEl.TryGetInt64(out var revision))
            {
                errorCode = "invalidRevision";
                return false;
            }

            if (revision < 0)
            {
                errorCode = "negativeRevision";
                return false;
            }

            if (!root.TryGetProperty("receiptId", out var receiptEl) || receiptEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(receiptEl.GetString(), out var receiptId) || receiptId == Guid.Empty)
            {
                errorCode = "emptyReceiptId";
                return false;
            }

            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            {
                errorCode = itemsEl.ValueKind == JsonValueKind.Null ? "nullItems" : "nullItems";
                return false;
            }

            if (!root.TryGetProperty("tombstones", out var tombsEl) || tombsEl.ValueKind != JsonValueKind.Array)
            {
                errorCode = tombsEl.ValueKind == JsonValueKind.Null ? "nullTombstones" : "nullTombstones";
                return false;
            }

            foreach (var el in itemsEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Null)
                {
                    errorCode = "nullItemElement";
                    return false;
                }
            }

            foreach (var el in tombsEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Null)
                {
                    errorCode = "nullTombstoneElement";
                    return false;
                }
            }

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
            };
            snapshot = JsonSerializer.Deserialize<DesiredSnapshotDto>(json, options);
            return TryValidate(snapshot, expectedDeviceId, out errorCode);
        }
        catch (JsonException)
        {
            errorCode = "invalidJson";
            return false;
        }
    }
}
