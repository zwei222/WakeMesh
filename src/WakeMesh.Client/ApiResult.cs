namespace WakeMesh.Client;

/// <summary>UI-distinguishable failure kinds for owner flows.</summary>
public enum ApiErrorKind
{
    None = 0,
    /// <summary>HTTP 401 — re-authenticate (owner token or device credential rejected).</summary>
    Unauthorized = 1,
    /// <summary>HTTP 403 — authenticated but not permitted.</summary>
    Forbidden = 2,
    /// <summary>HTTP 404.</summary>
    NotFound = 3,
    /// <summary>HTTP 409 — conflict (duplicate group content, receipt mismatch, etc.).</summary>
    Conflict = 4,
    /// <summary>HTTP 410 — device revoked or receipt expired.</summary>
    Gone = 5,
    /// <summary>HTTP 412 — If-Match version conflict; reload and retry.</summary>
    PreconditionFailed = 6,
    /// <summary>HTTP 428 — If-Match required.</summary>
    PreconditionRequired = 7,
    /// <summary>HTTP 422 — e.g. future revision ACK.</summary>
    UnprocessableEntity = 8,
    /// <summary>HTTP 400 or other client errors.</summary>
    BadRequest = 9,
    /// <summary>Operation canceled by caller.</summary>
    Canceled = 10,
    /// <summary>Request timed out.</summary>
    Timeout = 11,
    /// <summary>Transport / network failure.</summary>
    Network = 12,
    /// <summary>Redirect responses are refused (token/credential leak prevention).</summary>
    RedirectRefused = 13,
    /// <summary>HTTP 5xx.</summary>
    Server = 14,
    Unexpected = 15,
    /// <summary>2xx with malformed, null, or truncated JSON body.</summary>
    InvalidResponse = 16,
    /// <summary>IAccessTokenProvider threw; message is fixed (never provider exception text).</summary>
    TokenProviderFailed = 17,
    /// <summary>IDeviceCredentialStore threw during durable save after server accepted registration.</summary>
    CredentialPersistFailed = 18,
    /// <summary>IDeviceCredentialStore threw on read/delete paths.</summary>
    CredentialStoreFailed = 19,
    /// <summary>
    /// Operation ended before a definitive local outcome (e.g. register cancel in-flight before headers).
    /// Server side-effect may or may not exist; <see cref="ApiResult{T}.RecoveryNeeded"/> is true and
    /// <see cref="ApiResult{T}.ServerDeviceId"/> stays null when unknown.
    /// </summary>
    UnknownOutcome = 20,
}

/// <summary>Typed API outcome. Never embeds access tokens or device credentials in messages.</summary>
public sealed class ApiResult<T>
{
    private ApiResult(
        bool isSuccess,
        T? value,
        ApiErrorKind kind,
        int? statusCode,
        string? errorCode,
        string? message,
        Guid? serverDeviceId,
        bool recoveryNeeded)
    {
        IsSuccess = isSuccess;
        Value = value;
        Kind = kind;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
        ServerDeviceId = serverDeviceId;
        RecoveryNeeded = recoveryNeeded;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public ApiErrorKind Kind { get; }
    public int? StatusCode { get; }
    /// <summary>Server <c>error</c> field when present (safe codes only).</summary>
    public string? ErrorCode { get; }
    /// <summary>Safe diagnostic text — must not contain tokens/credentials.</summary>
    public string? Message { get; }
    /// <summary>
    /// When set, the server accepted a device registration (or similar) but the client could not finish
    /// local durable handling. Safe id only — never a credential.
    /// </summary>
    public Guid? ServerDeviceId { get; }
    /// <summary>True when the caller must run recovery (e.g. credential persist failed after register).</summary>
    public bool RecoveryNeeded { get; }

    public static ApiResult<T> Ok(T value) => new(true, value, ApiErrorKind.None, null, null, null, null, false);

    public static ApiResult<T> Fail(
        ApiErrorKind kind,
        int? statusCode = null,
        string? errorCode = null,
        string? message = null,
        Guid? serverDeviceId = null,
        bool recoveryNeeded = false,
        T? value = default)
        => new(false, value, kind, statusCode, errorCode, message, serverDeviceId, recoveryNeeded);

    public ApiResult<TOther> MapError<TOther>()
        => ApiResult<TOther>.Fail(Kind, StatusCode, ErrorCode, Message, ServerDeviceId, RecoveryNeeded);
}

public sealed class ApiResult
{
    private ApiResult(bool isSuccess, ApiErrorKind kind, int? statusCode, string? errorCode, string? message)
    {
        IsSuccess = isSuccess;
        Kind = kind;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool IsSuccess { get; }
    public ApiErrorKind Kind { get; }
    public int? StatusCode { get; }
    public string? ErrorCode { get; }
    public string? Message { get; }

    public static ApiResult Ok() => new(true, ApiErrorKind.None, null, null, null);

    public static ApiResult Fail(ApiErrorKind kind, int? statusCode = null, string? errorCode = null, string? message = null)
        => new(false, kind, statusCode, errorCode, message);

    public ApiResult<T> As<T>() => ApiResult<T>.Fail(Kind, StatusCode, ErrorCode, Message);
}
