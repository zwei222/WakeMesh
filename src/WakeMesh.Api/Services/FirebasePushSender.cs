using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using WakeMesh.Api.Data;

namespace WakeMesh.Api.Services;

/// <summary>
/// Production FCM sender via Firebase Admin SDK.
/// Credentials: server-only file path (WakeMesh:Fcm:CredentialsPath) or Application Default Credentials.
/// Never embeds service-account JSON in config/source. Unconfigured → NotConfigured (no fake success).
/// Logs fixed error codes/types only — never tokens, paths, private keys, or full exception text.
/// </summary>
public sealed class FirebasePushSender : IPushSender
{
    private readonly FcmOptions _options;
    private readonly ILogger<FirebasePushSender> _logger;
    private readonly object _initLock = new();
    private bool _initAttempted;
    private bool _available;

    public FirebasePushSender(IOptions<FcmOptions> options, ILogger<FirebasePushSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public async Task<PushSendResult> SendSettingsChangedAsync(
        string pushToken,
        Guid deviceId,
        long desiredRevision,
        bool notificationCapable,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pushToken))
        {
            return new PushSendResult(PushSendOutcome.InvalidToken, "emptyToken");
        }

        EnsureInitialized();
        if (!_available || FirebaseMessaging.DefaultInstance is null)
        {
            return new PushSendResult(PushSendOutcome.NotConfigured, "notConfigured");
        }

        var message = BuildSettingsChangedMessage(pushToken, deviceId, desiredRevision, notificationCapable);

        try
        {
            // FirebaseAdmin SendAsync does not take CancellationToken on all versions; honor cancel before call.
            cancellationToken.ThrowIfCancellationRequested();
            var messageId = await FirebaseMessaging.DefaultInstance.SendAsync(message).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return new PushSendResult(PushSendOutcome.TransientFailure, "emptyMessageId");
            }

            // Log only non-secret identifiers.
            _logger.LogInformation(
                "FCM accepted settingsChanged for device {DeviceId} revision {Revision} high={High}",
                deviceId,
                desiredRevision,
                notificationCapable);
            return new PushSendResult(PushSendOutcome.Accepted);
        }
        catch (FirebaseMessagingException ex)
        {
            var code = ClassifyFirebaseError(ex);
            _logger.LogWarning(
                "FCM send outcome {Outcome} code {ErrorCode} for device {DeviceId}",
                code.Outcome,
                code.ErrorCode,
                deviceId);
            return code;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fixed type name only — do not attach exception object (message may contain paths/secrets).
            _logger.LogWarning(
                "FCM send failed for device {DeviceId}: {ErrorType}",
                deviceId,
                ex.GetType().Name);
            return new PushSendResult(PushSendOutcome.TransientFailure, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Builds the exact <see cref="Message"/> handed to <c>FirebaseMessaging.SendAsync</c>.
    /// Extracted so tests can inspect the real SDK object (and its wire shape) instead of
    /// re-deriving the payload with a hand-written mock serializer.
    /// </summary>
    internal static Message BuildSettingsChangedMessage(
        string pushToken,
        Guid deviceId,
        long desiredRevision,
        bool notificationCapable)
    {
        // Minimal data payload only — no alarm times, labels, or credentials.
        return new Message
        {
            // FCM registration token. FirebaseAdmin 3.7.1's Fid is the *Firebase Installation ID*
            // (FCM REST v1: "Firebase Installation ID (FID) to send a message to"), not a new name
            // for the registration token — the client still obtains a registration token via
            // FirebaseMessaging.GetToken() (AndroidPushConfigurationProvider), so it belongs in the
            // token field. FCM REST v1 describes token as "Deprecated: Use fid instead. During the
            // transition period, this field also accepts a Firebase Installation ID (FID).
            // Registration token to send a message to." — i.e. the migration accepts the existing
            // registration token here; it never says fid accepts a registration token.
            // Token is [Obsolete] in the SDK ("Deprecated. Use Fid instead."), so the warning is
            // suppressed for this single assignment only. Fid stays null: CopyAndValidate() throws
            // when more than one recipient (Token/Fid/Topic/Condition) is set. Do not migrate to Fid
            // until the client acquires FIDs via onRegistered(); that is a separate client change.
            // Sources: FCM REST v1 reference and the FirebaseAdmin 3.7.1 SDK behaviour quoted above.
#pragma warning disable CS0618 // Message.Token is obsolete in FirebaseAdmin 3.7.1 (Fid = Firebase Installation ID)
            Token = pushToken,
#pragma warning restore CS0618
            Data = new Dictionary<string, string>
            {
                ["type"] = PushMessageTypes.SettingsChanged,
                ["deviceId"] = deviceId.ToString("D"),
                ["revision"] = desiredRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            // Data-only by design: a notification payload makes the Android SDK show the message in
            // the system tray when the app is backgrounded and skip OnMessageReceived — the callback
            // that shows WakeMesh's own visible notification and enqueues the WorkManager sync.
            // Whatever FCM's backend does with android.notification alone is unspecified, so no
            // notification field is sent at all; the client owns the visible notification.
            // High priority only when the device reported notification capability.
            Android = new AndroidConfig
            {
                Priority = notificationCapable ? Priority.High : Priority.Normal,
            },
            // APNS not required for current Android-first slice.
        };
    }

    /// <summary>
    /// Only clearly-unregistered tokens clear storage. InvalidArgument often means bad payload → retry/failed,
    /// not token clear.
    /// </summary>
    internal static PushSendResult ClassifyFirebaseError(FirebaseMessagingException ex)
    {
        if (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            return new PushSendResult(PushSendOutcome.InvalidToken, "Unregistered");
        }

        // InvalidArgument is ambiguous (bad payload vs bad token). Treat as transient/failed, not token clear.
        if (ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
        {
            return new PushSendResult(PushSendOutcome.TransientFailure, "InvalidArgument");
        }

        var messaging = ex.MessagingErrorCode?.ToString();
        if (!string.IsNullOrEmpty(messaging))
        {
            return new PushSendResult(PushSendOutcome.TransientFailure, messaging);
        }

        return new PushSendResult(PushSendOutcome.TransientFailure, ex.ErrorCode.ToString());
    }

    private void EnsureInitialized()
    {
        if (_initAttempted)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initAttempted)
            {
                return;
            }

            _initAttempted = true;

            // Feature disabled unless explicitly configured AND credentials resolve.
            if (!_options.Configured)
            {
                _available = false;
                return;
            }

            try
            {
                if (FirebaseApp.DefaultInstance is not null)
                {
                    _available = true;
                    return;
                }

                GoogleCredential credential;
                if (!string.IsNullOrWhiteSpace(_options.CredentialsPath))
                {
                    var path = _options.CredentialsPath.Trim();
                    if (!File.Exists(path))
                    {
                        // Do not log the path (may reveal layout).
                        _logger.LogWarning("FCM CredentialsPath missing; push remains disabled.");
                        _available = false;
                        return;
                    }

                    // Stream from server-only path via CredentialFactory — do not embed JSON in config/source.
                    var serviceAccount = CredentialFactory.FromFile<ServiceAccountCredential>(path);
                    credential = serviceAccount.ToGoogleCredential();
                }
                else
                {
                    // Application Default Credentials (e.g. GOOGLE_APPLICATION_CREDENTIALS env pointing at a file).
                    credential = GoogleCredential.GetApplicationDefault();
                }

                FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                });
                _available = FirebaseMessaging.DefaultInstance is not null;
            }
            catch (Exception ex)
            {
                // Missing ADC / bad file → feature disabled; API and MS login stay healthy.
                // Type only — exception text can include file paths.
                _logger.LogWarning(
                    "FCM credential initialization failed ({ErrorType}); push remains disabled.",
                    ex.GetType().Name);
                _available = false;
            }
        }
    }
}
