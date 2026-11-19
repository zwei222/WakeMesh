namespace WakeMesh.Api.Models;

public sealed record DeviceRegisterRequest(string? DisplayName);

public sealed record DeviceRegisterResponse(Guid Id, string DisplayName, string Credential, string Status);

public sealed record DeviceAckFailureDto(
    Guid ReceiptId,
    long Revision,
    Guid? ScheduleId,
    string Code,
    string? Message,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Owner device list row. <paramref name="HasSuccessfulSync"/> is a server projection of durable
/// sync evidence (a persisted success ACK for a receipt issued to this registration):
/// true = at least one successful sync ACK, false = none yet, null = server did not report it
/// (older API). Omitted-from-JSON therefore reads as null, never as "no ACK".
/// </summary>
public sealed record DeviceListItem(
    Guid Id,
    string DisplayName,
    string Status,
    long DesiredRevision,
    long AppliedRevision,
    string RetirementState,
    bool CancelConfirmed,
    DateTimeOffset? RevokedAt,
    bool? HasSuccessfulSync = null);

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

public sealed record SaveGroupResult(AlarmGroupDto Group, NotificationStatusDto Notification);

/// <summary>
/// Device registers its FCM token. Token is never echoed in owner device list/detail.
/// <paramref name="NotificationsCapable"/> defaults false when omitted (JSON / older clients).
/// </summary>
public sealed record PushTokenPutRequest(string Token, bool NotificationsCapable = false);

public sealed record PushTokenPutResponse(Guid DeviceId, DateTimeOffset UpdatedAt);
