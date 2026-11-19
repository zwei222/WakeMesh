using Microsoft.Extensions.Options;
using WakeMesh.Api.Models;

namespace WakeMesh.Api.Services;

public sealed class FcmOptions
{
    public const string SectionName = "WakeMesh:Fcm";

    /// <summary>
    /// When false, push is disabled (notConfigured). When true, sender still requires runtime credentials.
    /// </summary>
    public bool Configured { get; set; }

    /// <summary>
    /// Absolute path to a server-only service account JSON file. Prefer ADC via environment in production.
    /// Never put private key JSON into configuration values or source.
    /// </summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Max outbox rows processed per drain call.</summary>
    public int DrainBatchSize { get; set; } = 32;

    /// <summary>Max send attempts before discarding an outbox row.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>How long a drain claim holds an outbox row (seconds). Expired leases are reclaimable.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>
    /// Bounded periodic drain interval when configured (seconds). Not a hard real-time SLA;
    /// request-path and startup drains also run. Missing/invalid → default.
    /// </summary>
    public int DrainIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When FCM is not configured/available, background loop backs off up to this many seconds
    /// (prevents busy-loop). Does not guarantee always-on sleep precision.
    /// </summary>
    public int NotConfiguredBackoffMaxSeconds { get; set; } = 300;
}

public sealed class NotificationStatusService
{
    private readonly FcmOptions _options;
    private readonly IPushSender _sender;

    public NotificationStatusService(IOptions<FcmOptions> options, IPushSender sender)
    {
        _options = options.Value;
        _sender = sender;
    }

    public bool IsPushActive => _options.Configured && _sender.IsAvailable;

    public NotificationStatusDto GetStatus()
    {
        if (!_options.Configured || !_sender.IsAvailable)
        {
            return new NotificationStatusDto("notConfigured", "FCM is not configured; push was not attempted.");
        }

        return new NotificationStatusDto("ready", "FCM sender is available; delivery uses outbox.");
    }

    public static NotificationStatusDto NotConfigured()
        => new("notConfigured", "FCM is not configured; push was not attempted.");

    public static NotificationStatusDto Pending(string? detail = null)
        => new("pending", detail ?? "Push queued; FCM accept is not device apply.");

    public static NotificationStatusDto Accepted(string? detail = null)
        => new("accepted", detail ?? "FCM accepted message; device apply still requires desired/ACK.");

    public static NotificationStatusDto Failed(string? detail = null)
        => new("failed", detail ?? "Push send failed; settings were saved and outbox will retry.");

    public static NotificationStatusDto NoTargets(string? detail = null)
        => new("noTargets", detail ?? "No eligible devices with push tokens.");
}
