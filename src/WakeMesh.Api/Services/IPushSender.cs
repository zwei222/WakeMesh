namespace WakeMesh.Api.Services;

/// <summary>
/// Abstraction over FCM HTTP v1 send. Tests inject a mock; production uses Firebase Admin when credentials exist.
/// Never treat Accepted as device-applied ACK.
/// </summary>
public interface IPushSender
{
    /// <summary>True when the feature can attempt real or mock sends (not merely a config flag).</summary>
    bool IsAvailable { get; }

    /// <param name="notificationCapable">
    /// When true, Android high priority + visible notification may be used.
    /// When false, normal priority data-only (no high-priority abuse for non-capable devices).
    /// </param>
    Task<PushSendResult> SendSettingsChangedAsync(
        string pushToken,
        Guid deviceId,
        long desiredRevision,
        bool notificationCapable,
        CancellationToken cancellationToken);
}

public enum PushSendOutcome
{
    /// <summary>FCM/SDK accepted the message. Does not mean the device applied desired state.</summary>
    Accepted,
    /// <summary>Token is invalid/unregistered — caller must clear stored token only if still the sent value.</summary>
    InvalidToken,
    /// <summary>Transient or unknown failure — keep outbox pending for retry.</summary>
    TransientFailure,
    /// <summary>Sender/feature not configured — do not claim success.</summary>
    NotConfigured,
}

public sealed record PushSendResult(PushSendOutcome Outcome, string? ErrorCode = null);
