using System;

namespace WakeMesh.Mobile.Services.Push;

/// <summary>
/// Minimal settingsChanged push payload parsed from FCM data message.
/// Contains no personal alarm times, labels, or credentials.
/// </summary>
public sealed record PushMessagePayload(
    string Type,
    Guid DeviceId,
    long Revision);
