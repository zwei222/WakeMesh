namespace WakeMesh.Client.Models;

/// <summary>Wire DTOs matching WakeMesh.Api JSON contracts (camelCase). Client-owned copies; no API assembly reference.</summary>
public sealed record DeviceRegisterRequest(string? DisplayName);

public sealed record DeviceRegisterResponse(Guid Id, string DisplayName, string Credential, string Status);

public sealed record DeviceAckFailureDto(
    Guid ReceiptId,
    long Revision,
    Guid? ScheduleId,
    string Code,
    string? Message,
    DateTimeOffset ReceivedAt);

public sealed record DeviceListItem(
    Guid Id,
    string DisplayName,
    string Status,
    long DesiredRevision,
    long AppliedRevision,
    string RetirementState,
    bool CancelConfirmed,
    DateTimeOffset? RevokedAt);

public sealed record DeviceDetailDto(
    Guid Id,
    string DisplayName,
    string Status,
    long DesiredRevision,
    long AppliedRevision,
    string RetirementState,
    bool CancelConfirmed,
    DateTimeOffset? RevokedAt,
    IReadOnlyList<DeviceAckFailureDto> RecentFailures);

public sealed record AlarmTargetDto(
    Guid? Id,
    Guid DeviceId,
    string Time,
    string SoundKey,
    int Volume,
    bool Vibrate,
    bool Enabled);

public sealed record AlarmGroupDto(
    string Id,
    string Label,
    bool Enabled,
    long Version,
    DateTimeOffset? DeletedAt,
    string Repeat,
    IReadOnlyList<int>? Days,
    string? Date,
    string TimeZone,
    bool ExcludeHolidays,
    IReadOnlyList<string> ExcludedDates,
    IReadOnlyList<AlarmTargetDto> Targets);

public sealed record AlarmGroupWriteRequest(
    string? Id,
    string Label,
    bool Enabled,
    string Repeat,
    IReadOnlyList<int>? Days,
    string? Date,
    string TimeZone,
    bool ExcludeHolidays,
    IReadOnlyList<string>? ExcludedDates,
    IReadOnlyList<AlarmTargetDto> Targets);

public sealed record DesiredItemDto(
    Guid ScheduleId,
    string GroupId,
    string Label,
    bool GroupEnabled,
    string Repeat,
    IReadOnlyList<int>? Days,
    string? Date,
    string TimeZone,
    bool ExcludeHolidays,
    IReadOnlyList<string> ExcludedDates,
    string Time,
    string SoundKey,
    int Volume,
    bool Vibrate,
    bool Enabled,
    long ModifiedRevision);

public sealed record DesiredTombstoneDto(Guid ScheduleId);

public sealed record DesiredSnapshotDto(
    Guid DeviceId,
    long Revision,
    Guid ReceiptId,
    IReadOnlyList<DesiredItemDto> Items,
    IReadOnlyList<DesiredTombstoneDto> Tombstones);

public sealed record AckFailureDto(Guid? ScheduleId, string Code, string? Message);

public sealed record AckRequest(
    Guid ReceiptId,
    long Revision,
    string Result,
    IReadOnlyList<AckFailureDto>? Failures,
    DateTimeOffset? ClientReportedAt);

public sealed record AckResponse(Guid DeviceId, long AppliedRevision, string Status);

public sealed record NotificationStatusDto(string State, string? Detail);

/// <summary>Server accepted the group write. Does NOT mean target devices applied the schedule.</summary>
public sealed record GroupSaveOutcome(AlarmGroupDto Group, NotificationStatusDto Notification);

/// <summary>Register FCM token for a device. Requires owner token + device credential.</summary>
/// <remarks>NotificationsCapable defaults to false when omitted (JSON / older clients).</remarks>
public sealed record PushTokenPutRequest(string Token, bool NotificationsCapable = false);

/// <summary>Token is never echoed; only device id and update timestamp.</summary>
public sealed record PushTokenPutResponse(Guid DeviceId, DateTimeOffset UpdatedAt);
