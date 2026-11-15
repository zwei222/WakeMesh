using System.Reflection;
using FirebaseAdmin.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WakeMesh.Api.Data;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T150: the production settings-changed FCM message must be data-only.
///
/// When the
/// delivered bundle carries a notification payload (<c>gcm.n.e=1</c>) and the app is in the
/// background, the Android SDK shows it in the system tray and returns without calling
/// <c>onMessageReceived</c>. WakeMesh relies on that callback to show its own visible notification
/// and to enqueue the WorkManager sync, so any server-side notification payload silently converts
/// the sync into a "tap the tray to run" flow. Sending data-only removes the ambiguity.
///
/// These tests inspect the real FirebaseAdmin 3.7.1 <see cref="Message"/> produced by the
/// production builder — no hand-written mock serializer — and then serialize it with Newtonsoft
/// (the same serializer family FirebaseAdmin uses) to assert the wire shape.
///
/// The client's registration token belongs in
/// <see cref="Message.Token"/> (JSON <c>token</c>), not <see cref="Message.Fid"/>. FirebaseAdmin
/// 3.7.1's <c>Fid</c> is the Firebase Installation ID — a different identifier with a different
/// client acquisition path — so <c>Fid</c> must stay null. The deprecated <c>Token</c> field
/// remains supported for registration tokens during the documented transition period.
/// </summary>
public sealed class T150FcmDataOnlyPayloadTests
{
    private const string SyntheticToken = "synthetic-fcm-registration-token";
    private static readonly Guid Device = Guid.Parse("3f0c9c1e-8b6a-4c2d-9f11-2a5b7d4e6c80");
    private const long Revision = 42;

    /// <summary>
    /// Newtonsoft is what FirebaseAdmin uses for the FCM body; NullValueHandling.Ignore makes the
    /// result comparable to the wire payload (null members are simply absent).
    /// </summary>
    private static readonly JsonSerializerSettings WireSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static Message Build(bool notificationCapable) =>
        FirebasePushSender.BuildSettingsChangedMessage(SyntheticToken, Device, Revision, notificationCapable);

    private static JObject Wire(bool notificationCapable) =>
        JObject.Parse(JsonConvert.SerializeObject(Build(notificationCapable), WireSettings));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Message_Has_No_Notification_Payload_On_Real_Sdk_Types(bool notificationCapable)
    {
        var message = Build(notificationCapable);

        // Real SDK types, real property graph — not a stand-in DTO.
        Assert.IsType<Message>(message);
        Assert.IsType<AndroidConfig>(message.Android);

        // Root-level notification and android-level notification must both be absent.
        Assert.Null(message.Notification);
        Assert.Null(message.Android!.Notification);

        // Priority is still capability-driven and untouched by the data-only change.
        Assert.Equal(
            notificationCapable ? Priority.High : Priority.Normal,
            message.Android.Priority);

        // Recipient contract on the real SDK object: registration token in the (obsolete but
        // still supported) Token field; Fid — a Firebase Installation ID — left null. Never both:
        // CopyAndValidate() throws when more than one recipient field is set.
#pragma warning disable CS0618 // Message.Token is obsolete in FirebaseAdmin 3.7.1 (replaced by Fid = FID)
        Assert.Equal(SyntheticToken, message.Token);
#pragma warning restore CS0618
        Assert.Null(message.Fid);

        // Nothing platform-specific other than Android is populated for this slice.
        Assert.Null(message.Apns);
        Assert.Null(message.Webpush);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wire_Json_Has_No_Notification_Key_Anywhere(bool notificationCapable)
    {
        var json = Wire(notificationCapable);
        var raw = json.ToString(Formatting.None);

        Assert.False(json.ContainsKey("notification"), $"root notification leaked: {raw}");
        Assert.False(
            json["android"]!.ToObject<JObject>()!.ContainsKey("notification"),
            $"android notification leaked: {raw}");

        // Exact key surface: root {token, data, android}, android {priority}.
        // "token" (not "fid") is the corrected oracle — see the class remarks.
        Assert.Equal(
            new[] { "android", "data", "token" },
            json.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "priority" },
            json["android"]!.ToObject<JObject>()!.Properties()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Notification-only fields (server copy that used to duplicate the client's own text).
        foreach (var forbidden in new[]
                 {
                     "title", "body", "channel_id", "channelId", "wakemesh_settings",
                     "apns", "webpush", "fcm_options",
                 })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", raw);
        }
        Assert.DoesNotContain("設定変更", raw);
        Assert.DoesNotContain("アラーム設定が更新されました", raw);
    }

    [Theory]
    [InlineData(true, "high")]
    [InlineData(false, "normal")]
    public void Wire_Json_Keeps_Priority_And_Fixed_Data_Only(bool notificationCapable, string expectedPriority)
    {
        var json = Wire(notificationCapable);
        var raw = json.ToString(Formatting.None);

        // FCM registration token goes in the (obsolete but supported) "token" field. FirebaseAdmin
        // 3.7.1 did not rename Token → Fid: Fid is the Firebase Installation ID, which this payload
        // never carries (the API also rejects both recipient fields together).
        Assert.Equal(SyntheticToken, json.Value<string>("token"));
        Assert.Null(json["fid"]);
        Assert.Equal(expectedPriority, json["android"]!.Value<string>("priority"));

        // Exactly the three fixed data keys — no alarm labels, times, or server bookkeeping.
        var data = json["data"]!.ToObject<JObject>()!;
        Assert.Equal(
            new[] { "deviceId", "revision", "type" },
            data.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(PushMessageTypes.SettingsChanged, data.Value<string>("type"));
        Assert.Equal(Device.ToString("D"), data.Value<string>("deviceId"));
        Assert.Equal(
            Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            data.Value<string>("revision"));

        // No personal scheduling content and no credential material of any kind.
        foreach (var forbidden in new[]
                 {
                     "label", "alarm", "soundKey", "volume", "timeZone", "07:00",
                     "credential", "Authorization", "private_key", "apns",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Message_Data_Is_The_Three_Fixed_Keys()
    {
        var data = Build(true).Data;

        Assert.NotNull(data);
        Assert.Equal(3, data.Count);
        Assert.Equal(
            new[] { "deviceId", "revision", "type" },
            data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Send_Path_Really_Sends_The_Inspected_Message()
    {
        // Guard against an unused helper: SendSettingsChangedAsync must call the builder whose
        // output the tests above inspect. Verified on the compiled IL (the call sits inside the
        // generated async state machine, so the stub plus its MoveNext bodies are scanned).
        var builder = typeof(FirebasePushSender).GetMethod(
            "BuildSettingsChangedMessage",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(builder);
        Assert.Equal(typeof(Message), builder!.ReturnType);

        var stub = typeof(FirebasePushSender).GetMethod(
            nameof(FirebasePushSender.SendSettingsChangedAsync),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(stub);

        var token = BitConverter.GetBytes(builder.MetadataToken);
        var haystacks = new List<byte[]> { stub!.GetMethodBody()!.GetILAsByteArray()! };
        haystacks.AddRange(typeof(FirebasePushSender)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Where(t => t.Name.Contains(nameof(FirebasePushSender.SendSettingsChangedAsync), StringComparison.Ordinal))
            .Select(t => t.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(m => m is not null)
            .Select(m => m!.GetMethodBody()!.GetILAsByteArray()!));

        var wired = haystacks.Any(il => Contains(il, token));
        Assert.True(
            wired,
            "SendSettingsChangedAsync does not call BuildSettingsChangedMessage; the payload tests would be vacuous.");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
