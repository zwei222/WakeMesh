using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WakeMesh.Client.Http;
using WakeMesh.Client.Models;

namespace WakeMesh.Client;

/// <summary>
/// Shared owner/device API client. Distinguishes server-save success from target-device apply (ACK).
/// Does not log or embed access tokens or device credentials in exceptions/messages.
/// </summary>
public sealed class WakeMeshApiClient : IDisposable
{
    public const string DeviceCredentialHeaderName = PinnedHostHandler.DeviceCredentialHeaderName;

    /// <summary>Bounded time to finish body read + durable credential save after register headers are accepted.</summary>
    public static readonly TimeSpan RegisterCompletionTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly IDeviceCredentialStore? _credentials;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Production constructor: validates HTTPS base address and installs no-redirect + host pin handlers.
    /// Does not accept an arbitrary <see cref="HttpClient"/> (redirect-safe factory only).
    /// </summary>
    public WakeMeshApiClient(
        Uri httpsBaseAddress,
        IAccessTokenProvider tokens,
        IDeviceCredentialStore? credentials = null,
        TimeSpan? timeout = null)
        : this(WakeMeshHttpClientFactory.Create(httpsBaseAddress, timeout), tokens, credentials, ownsHttp: true)
    {
    }

    /// <summary>
    /// Preferred production factory: HTTPS base URL + token provider + optional credential store only.
    /// </summary>
    public static WakeMeshApiClient Create(
        string httpsBaseUrl,
        IAccessTokenProvider tokenProvider,
        IDeviceCredentialStore? credentialStore = null,
        TimeSpan? timeout = null)
        => new(HttpsBaseAddress.RequireHttps(httpsBaseUrl), tokenProvider, credentialStore, timeout);

    /// <summary>
    /// Preferred production factory (Uri overload).
    /// </summary>
    public static WakeMeshApiClient Create(
        Uri httpsBaseAddress,
        IAccessTokenProvider tokenProvider,
        IDeviceCredentialStore? credentialStore = null,
        TimeSpan? timeout = null)
        => new(httpsBaseAddress, tokenProvider, credentialStore, timeout);

    /// <summary>
    /// Test / in-process transport injection. Not part of the public production surface.
    /// Callers must not pass an auto-redirecting client intended for production secrets.
    /// </summary>
    internal WakeMeshApiClient(
        HttpClient httpClient,
        IAccessTokenProvider tokens,
        IDeviceCredentialStore? credentials = null,
        bool ownsHttp = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _credentials = credentials;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Test helper — same as the internal HttpClient constructor.</summary>
    internal static WakeMeshApiClient CreateForTests(
        HttpClient httpClient,
        IAccessTokenProvider tokens,
        IDeviceCredentialStore? credentials = null,
        bool ownsHttp = false)
        => new(httpClient, tokens, credentials, ownsHttp);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // --- Devices (owner) ---

    public Task<ApiResult<IReadOnlyList<DeviceListItem>>> ListDevicesAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DeviceListItem>>(HttpMethod.Get, "api/v1/devices", body: null, deviceIdForCredential: null, cancellationToken);

    public Task<ApiResult<DeviceDetailDto>> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendAsync<DeviceDetailDto>(HttpMethod.Get, $"api/v1/devices/{deviceId:D}", body: null, deviceIdForCredential: null, cancellationToken);

    /// <summary>
    /// Register device. After the server accepts the response headers, body parse and durable credential
    /// persistence run under a bounded independent cancellation token so a caller cancel cannot drop the
    /// one-time credential after server success.
    /// </summary>
    public async Task<ApiResult<DeviceRegisterResponse>> RegisterDeviceAsync(
        string? displayName,
        bool persistCredential = true,
        CancellationToken cancellationToken = default)
    {
        // Truly pre-send cancel: request never left the client — no server ambiguity.
        if (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Canceled, message: "canceled");
        }

        var sendStarted = false;
        try
        {
            using var request = await BuildRequestAsync(
                    HttpMethod.Post,
                    "api/v1/devices",
                    new DeviceRegisterRequest(displayName),
                    deviceIdForCredential: null,
                    extraHeaders: null,
                    cancellationToken)
                .ConfigureAwait(false);

            // Caller cancel still applies until headers are received. Once SendAsync begins,
            // server create may or may not have happened — cancel maps to UnknownOutcome.
            sendStarted = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // Server has accepted the HTTP exchange far enough to return headers.
            // Finish body + durable save under a bounded token independent of the caller.
            using var completionCts = new CancellationTokenSource(RegisterCompletionTimeout);
            var completionToken = completionCts.Token;

            if (!response.IsSuccessStatusCode)
            {
                return await ReadResultAsync<DeviceRegisterResponse>(response, completionToken).ConfigureAwait(false);
            }

            string? text;
            try
            {
                text = await response.Content.ReadAsStringAsync(completionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.Timeout,
                    (int)response.StatusCode,
                    "registerBodyTimeout",
                    "registerBodyTimeout",
                    recoveryNeeded: true);
            }
            catch (Exception)
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.InvalidResponse,
                    (int)response.StatusCode,
                    "registerBodyReadFailed",
                    "registerBodyReadFailed",
                    recoveryNeeded: true);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.InvalidResponse,
                    (int)response.StatusCode,
                    "emptyBody",
                    "emptyBody",
                    recoveryNeeded: true);
            }

            DeviceRegisterResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DeviceRegisterResponse>(text, JsonOptions);
            }
            catch (JsonException)
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.InvalidResponse,
                    (int)response.StatusCode,
                    "invalidJson",
                    "invalidJson",
                    recoveryNeeded: true);
            }

            if (parsed is null || parsed.Id == Guid.Empty || string.IsNullOrWhiteSpace(parsed.Credential))
            {
                return ApiResult<DeviceRegisterResponse>.Fail(
                    ApiErrorKind.InvalidResponse,
                    (int)response.StatusCode,
                    "invalidRegisterPayload",
                    "invalidRegisterPayload",
                    serverDeviceId: parsed is null || parsed.Id == Guid.Empty ? null : parsed.Id,
                    recoveryNeeded: true);
            }

            if (persistCredential && _credentials is not null)
            {
                try
                {
                    await _credentials.SaveCredentialAsync(parsed.Id, parsed.Credential, completionToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ApiResult<DeviceRegisterResponse>.Fail(
                        ApiErrorKind.CredentialPersistFailed,
                        (int)response.StatusCode,
                        "credentialPersistTimeout",
                        "credentialPersistFailed",
                        serverDeviceId: parsed.Id,
                        recoveryNeeded: true,
                        value: parsed);
                }
                catch (Exception)
                {
                    // Fixed classification — never surface store exception text (may contain paths/secrets).
                    return ApiResult<DeviceRegisterResponse>.Fail(
                        ApiErrorKind.CredentialPersistFailed,
                        (int)response.StatusCode,
                        "credentialPersistFailed",
                        "credentialPersistFailed",
                        serverDeviceId: parsed.Id,
                        recoveryNeeded: true,
                        value: parsed);
                }
            }

            return ApiResult<DeviceRegisterResponse>.Ok(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!sendStarted)
            {
                // Cancel during BuildRequest (token provider etc.) before the HTTP send — not ambiguous.
                return ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Canceled, message: "canceled");
            }

            // In-flight cancel before headers: server may already have created the device.
            // Do not claim recovery is unnecessary; serverDeviceId remains unknown/null.
            return ApiResult<DeviceRegisterResponse>.Fail(
                ApiErrorKind.UnknownOutcome,
                errorCode: "unknownOutcome",
                message: "unknownOutcome",
                serverDeviceId: null,
                recoveryNeeded: true);
        }
        catch (OperationCanceledException)
        {
            return ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Timeout, message: "timeout");
        }
        catch (RedirectRefusedException)
        {
            return ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.RedirectRefused, message: "redirectRefused");
        }
        catch (HttpRequestException)
        {
            return ApiResult<DeviceRegisterResponse>.Fail(ApiErrorKind.Network, message: "network");
        }
        catch (Exception ex) when (IsTokenProviderException(ex))
        {
            return ApiResult<DeviceRegisterResponse>.Fail(
                ApiErrorKind.TokenProviderFailed,
                errorCode: "tokenProviderFailed",
                message: "tokenProviderFailed");
        }
    }

    /// <summary>Begin graceful retire. Local cancel remains pending until target ACK.</summary>
    public Task<ApiResult> RetireDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendEmptyAsync(HttpMethod.Post, $"api/v1/devices/{deviceId:D}/retire", body: null, deviceIdForCredential: null, cancellationToken);

    /// <summary>Immediate revoke. Owner UI should show local cancel as unconfirmed until separately verified.</summary>
    public Task<ApiResult> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendEmptyAsync(HttpMethod.Post, $"api/v1/devices/{deviceId:D}/revoke", body: null, deviceIdForCredential: null, cancellationToken);

    // --- Alarm groups (owner) — save success ≠ target applied ---

    public Task<ApiResult<IReadOnlyList<AlarmGroupDto>>> ListAlarmGroupsAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<AlarmGroupDto>>(HttpMethod.Get, "api/v1/alarm-groups", body: null, deviceIdForCredential: null, cancellationToken);

    public Task<ApiResult<GroupSaveOutcome>> CreateAlarmGroupAsync(
        AlarmGroupWriteRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupSaveOutcome>(HttpMethod.Post, "api/v1/alarm-groups", request, deviceIdForCredential: null, cancellationToken);

    public Task<ApiResult<GroupSaveOutcome>> ReplaceAlarmGroupAsync(
        string groupId,
        AlarmGroupWriteRequest request,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string> { ["If-Match"] = ifMatchVersion.ToString() };
        return SendAsync<GroupSaveOutcome>(
            HttpMethod.Put,
            $"api/v1/alarm-groups/{Uri.EscapeDataString(groupId)}",
            request,
            deviceIdForCredential: null,
            cancellationToken,
            extraHeaders: headers);
    }

    public Task<ApiResult> DeleteAlarmGroupAsync(
        string groupId,
        long ifMatchVersion,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string> { ["If-Match"] = ifMatchVersion.ToString() };
        return SendEmptyAsync(
            HttpMethod.Delete,
            $"api/v1/alarm-groups/{Uri.EscapeDataString(groupId)}",
            body: null,
            deviceIdForCredential: null,
            cancellationToken,
            extraHeaders: headers);
    }

    // --- Push token (owner token + device credential) ---

    /// <summary>
    /// Register or replace the FCM registration token for a device.
    /// Requires owner access token and the target device credential. Token is never returned by the API.
    /// Compatibility overload: notificationsCapable defaults to false.
    /// </summary>
    public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(
        Guid deviceId,
        string token,
        CancellationToken cancellationToken = default)
        => PutPushTokenAsync(deviceId, token, notificationsCapable: false, cancellationToken);

    /// <summary>
    /// Register or replace the FCM registration token and report notification capability.
    /// When <paramref name="notificationsCapable"/> is true, server may use high-priority FCM;
    /// otherwise normal priority.
    /// </summary>
    public Task<ApiResult<PushTokenPutResponse>> PutPushTokenAsync(
        Guid deviceId,
        string token,
        bool notificationsCapable,
        CancellationToken cancellationToken = default)
        => SendAsync<PushTokenPutResponse>(
            HttpMethod.Put,
            $"api/v1/devices/{deviceId:D}/push-token",
            new PushTokenPutRequest(token, notificationsCapable),
            deviceIdForCredential: deviceId,
            cancellationToken);

    /// <summary>Clear the FCM registration token for a device.</summary>
    public Task<ApiResult> DeletePushTokenAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendEmptyAsync(
            HttpMethod.Delete,
            $"api/v1/devices/{deviceId:D}/push-token",
            body: null,
            deviceIdForCredential: deviceId,
            cancellationToken);

    // --- Desired / ACK (owner token + device credential) ---

    public async Task<ApiResult<DesiredSnapshotDto>> GetDesiredAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync<JsonElement?>(
                HttpMethod.Get,
                $"api/v1/devices/{deviceId:D}/desired",
                body: null,
                deviceIdForCredential: deviceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!raw.IsSuccess)
        {
            return raw.MapError<DesiredSnapshotDto>();
        }

        if (raw.Value is null || raw.Value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return ApiResult<DesiredSnapshotDto>.Fail(
                ApiErrorKind.InvalidResponse,
                raw.StatusCode,
                "nullBody",
                "nullBody");
        }

        string json;
        try
        {
            json = raw.Value.Value.GetRawText();
        }
        catch (Exception)
        {
            return ApiResult<DesiredSnapshotDto>.Fail(
                ApiErrorKind.InvalidResponse,
                raw.StatusCode,
                "invalidJson",
                "invalidJson");
        }

        if (!Sync.DesiredSnapshotValidator.TryValidateRaw(json, deviceId, out var snapshot, out var code))
        {
            return ApiResult<DesiredSnapshotDto>.Fail(
                ApiErrorKind.InvalidResponse,
                raw.StatusCode,
                code,
                code);
        }

        return ApiResult<DesiredSnapshotDto>.Ok(snapshot!);
    }

    public Task<ApiResult<AckResponse>> PostAckAsync(
        Guid deviceId,
        AckRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<AckResponse>(
            HttpMethod.Post,
            $"api/v1/devices/{deviceId:D}/acks",
            request,
            deviceIdForCredential: deviceId,
            cancellationToken);

    // --- internals ---

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        Guid? deviceIdForCredential,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        try
        {
            using var request = await BuildRequestAsync(method, relativePath, body, deviceIdForCredential, extraHeaders, cancellationToken)
                .ConfigureAwait(false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return await ReadResultAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(ApiErrorKind.Canceled, message: "canceled");
        }
        catch (OperationCanceledException)
        {
            return ApiResult<T>.Fail(ApiErrorKind.Timeout, message: "timeout");
        }
        catch (RedirectRefusedException)
        {
            return ApiResult<T>.Fail(ApiErrorKind.RedirectRefused, message: "redirectRefused");
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Fail(ApiErrorKind.Network, message: "network");
        }
        catch (Exception ex) when (IsTokenProviderException(ex))
        {
            return ApiResult<T>.Fail(
                ApiErrorKind.TokenProviderFailed,
                errorCode: "tokenProviderFailed",
                message: "tokenProviderFailed");
        }
        catch (Exception ex) when (IsCredentialStoreException(ex))
        {
            return ApiResult<T>.Fail(
                ApiErrorKind.CredentialStoreFailed,
                errorCode: "credentialStoreFailed",
                message: "credentialStoreFailed");
        }
    }

    private async Task<ApiResult> SendEmptyAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        Guid? deviceIdForCredential,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var typed = await SendAsync<JsonElement?>(method, relativePath, body, deviceIdForCredential, cancellationToken, extraHeaders)
            .ConfigureAwait(false);
        if (typed.IsSuccess)
        {
            return ApiResult.Ok();
        }

        return ApiResult.Fail(typed.Kind, typed.StatusCode, typed.ErrorCode, typed.Message);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        Guid? deviceIdForCredential,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        // Always relative to BaseAddress so host pin stays on the configured API host.
        var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        string? token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TokenProviderException(ex);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (deviceIdForCredential is Guid deviceId)
        {
            if (_credentials is null)
            {
                throw new CredentialStoreException(
                    new InvalidOperationException("Device credential store is required for device-authenticated calls."));
            }

            string? credential;
            try
            {
                credential = await _credentials.GetCredentialAsync(deviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CredentialStoreException(ex);
            }

            if (string.IsNullOrWhiteSpace(credential))
            {
                // Leave header absent → API returns 401; UI maps Unauthorized.
            }
            else
            {
                request.Headers.TryAddWithoutValidation(DeviceCredentialHeaderName, credential);
            }
        }

        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        return request;
    }

    private static async Task<ApiResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400)
        {
            return ApiResult<T>.Fail(ApiErrorKind.RedirectRefused, code, "redirectRefused", "redirectRefused");
        }

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.ResetContent)
        {
            if (typeof(T) == typeof(JsonElement?) || typeof(T) == typeof(object))
            {
                return ApiResult<T>.Ok(default!);
            }

            // Successful empty body for non-payload operations routed through generic reader.
            return ApiResult<T>.Ok(default!);
        }

        string? text = null;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(ApiErrorKind.Canceled, message: "canceled");
        }
        catch
        {
            text = null;
        }

        if (response.IsSuccessStatusCode)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (typeof(T) == typeof(JsonElement?) || typeof(T) == typeof(object))
                {
                    return ApiResult<T>.Ok(default!);
                }

                return ApiResult<T>.Fail(ApiErrorKind.InvalidResponse, code, "emptyBody", "emptyBody");
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(text, JsonOptions);
                if (value is null && !typeof(T).IsValueType)
                {
                    return ApiResult<T>.Fail(ApiErrorKind.InvalidResponse, code, "nullBody", "nullBody");
                }

                return ApiResult<T>.Ok(value!);
            }
            catch (JsonException)
            {
                return ApiResult<T>.Fail(ApiErrorKind.InvalidResponse, code, "invalidJson", "invalidJson");
            }
        }

        var kind = MapStatus(response.StatusCode);
        var errorCode = TryReadErrorCode(text);
        // Never include response bodies that might echo secrets in Message for auth failures.
        var safeMessage = errorCode ?? kind.ToString();
        return ApiResult<T>.Fail(kind, code, errorCode, safeMessage);
    }

    private static ApiErrorKind MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ApiErrorKind.Unauthorized,
        HttpStatusCode.Forbidden => ApiErrorKind.Forbidden,
        HttpStatusCode.NotFound => ApiErrorKind.NotFound,
        HttpStatusCode.Conflict => ApiErrorKind.Conflict,
        HttpStatusCode.Gone => ApiErrorKind.Gone,
        HttpStatusCode.PreconditionFailed => ApiErrorKind.PreconditionFailed,
        HttpStatusCode.UnprocessableEntity => ApiErrorKind.UnprocessableEntity,
        HttpStatusCode.BadRequest => ApiErrorKind.BadRequest,
        HttpStatusCode.RequestTimeout => ApiErrorKind.Timeout,
        _ when (int)status == 428 => ApiErrorKind.PreconditionRequired,
        _ when (int)status >= 500 => ApiErrorKind.Server,
        _ => ApiErrorKind.Unexpected,
    };

    private static string? TryReadErrorCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String)
            {
                return err.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    private static bool IsTokenProviderException(Exception ex)
        => ex is TokenProviderException || ex.GetType().Name.Contains("TokenProvider", StringComparison.Ordinal);

    private static bool IsCredentialStoreException(Exception ex)
        => ex is CredentialStoreException;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        return o;
    }

    private sealed class TokenProviderException : Exception
    {
        public TokenProviderException(Exception inner) : base(null, inner)
        {
        }
    }

    private sealed class CredentialStoreException : Exception
    {
        public CredentialStoreException(Exception inner) : base(null, inner)
        {
        }
    }
}
