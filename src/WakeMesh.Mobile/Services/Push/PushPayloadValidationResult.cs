namespace WakeMesh.Mobile.Services.Push;

public enum PushValidationStatus
{
    Valid,
    MissingData,
    InvalidType,
    InvalidDeviceId,
    InvalidRevision,
    DeviceMismatch,
    NotRegisteredOrRecoveryNeeded,
    ScopeMismatch
}

public sealed record PushPayloadValidationResult(
    PushValidationStatus Status,
    PushMessagePayload? Payload = null,
    string? ErrorReason = null)
{
    public bool IsValid => Status == PushValidationStatus.Valid;

    public static PushPayloadValidationResult Valid(PushMessagePayload payload) =>
        new(PushValidationStatus.Valid, payload);

    public static PushPayloadValidationResult Failed(PushValidationStatus status, string reason) =>
        new(status, null, reason);
}
