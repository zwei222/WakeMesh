using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services.Push;

public interface IPushManager
{
    PushServiceStatus Status { get; }
    string? CurrentToken { get; }
    string? LastRegisteredToken { get; }
    bool? LastSentNotificationsCapable { get; }
    string? LastError { get; }
    bool IsConfigured { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task OnNewTokenReceivedAsync(string token, CancellationToken cancellationToken = default);
    Task OnNotificationPermissionChangedAsync(CancellationToken cancellationToken = default);
    Task<PushPayloadValidationResult> OnPushMessageReceivedAsync(IReadOnlyDictionary<string, string>? data, CancellationToken cancellationToken = default);
    Task<bool> UnregisterTokenAsync(CancellationToken cancellationToken = default);

    event EventHandler? StatusChanged;
}
