namespace WakeMesh.Api.Data;

public enum DeviceStatus
{
    Pending = 0,
    Active = 1,
    Revoked = 2,
    Retiring = 3
}

public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] CredentialHash { get; set; } = Array.Empty<byte>();
    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;
    public long DesiredRevision { get; set; }
    public long AppliedRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>True when local cancel was confirmed via success ACK while retiring.</summary>
    public bool CancelConfirmed { get; set; }
    /// <summary>FCM registration token. Never returned in owner list/detail DTOs or logs.</summary>
    public string? PushToken { get; set; }
    public DateTimeOffset? PushTokenUpdatedAt { get; set; }
    /// <summary>
    /// Device reported it can show notifications. Server uses high-priority FCM only when true.
    /// Default false for backward-compatible token puts that omit the field.
    /// </summary>
    public bool PushNotificationsCapable { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>
/// Durable push work item. Settings commits enqueue here; FCM accept is separate from device apply ACK.
/// </summary>
public sealed class PushOutboxEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public long DesiredRevision { get; set; }
    /// <summary>Fixed wire type, e.g. settingsChanged.</summary>
    public string MessageType { get; set; } = PushMessageTypes.SettingsChanged;
    /// <summary>pending | sending | accepted | failed | discarded</summary>
    public string Status { get; set; } = PushOutboxStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    /// <summary>Non-secret error class only (never tokens/payloads).</summary>
    public string? LastErrorCode { get; set; }
    /// <summary>Drain claim owner (opaque). Completions must match this owner.</summary>
    public string? LeaseOwner { get; set; }
    /// <summary>When the claim expires; expired sending rows are reclaimable after crash.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

public static class PushMessageTypes
{
    public const string SettingsChanged = "settingsChanged";
}

public static class PushOutboxStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Accepted = "accepted";
    public const string Failed = "failed";
    public const string Discarded = "discarded";
}

public sealed class AlarmGroupEntity
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
    public string Repeat { get; set; } = "weekly"; // weekly | once
    public string DaysOrDateJson { get; set; } = "[]";
    public string TimeZone { get; set; } = "Asia/Tokyo";
    public bool ExcludeHolidays { get; set; }
    public string ExcludedDatesJson { get; set; } = "[]";
    public uint RowVersion { get; set; }
    public List<DeviceScheduleEntity> Targets { get; set; } = new();
}

public sealed class DeviceScheduleEntity
{
    public Guid Id { get; set; }
    public string GroupId { get; set; } = "";
    public Guid DeviceId { get; set; }
    public string Time { get; set; } = "07:00"; // HH:mm
    public string SoundKey { get; set; } = "default";
    public int Volume { get; set; } = 80;
    public bool Vibrate { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public long ModifiedRevision { get; set; }
    public AlarmGroupEntity? Group { get; set; }
}

public sealed class DesiredTombstoneEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ScheduleId { get; set; }
    public long CreatedAtRevision { get; set; }
    public bool Cleared { get; set; }
}

public sealed class SnapshotReceiptEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool ConsumedSuccess { get; set; }
}

public sealed class AckRecordEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ReceiptId { get; set; }
    public long Revision { get; set; }
    public string Result { get; set; } = ""; // success | failure
    public string FailuresJson { get; set; } = "[]";
    public DateTimeOffset ReceivedAt { get; set; }
}
