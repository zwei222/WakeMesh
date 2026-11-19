using System;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

/// <summary>
/// Reason a durable auth session was explicitly ended by a completed user operation.
/// Intermediate evaluations (Initialize / Reload / silent failure) never emit these.
/// </summary>
public enum AuthSessionEndReason
{
    ExplicitSignOut,
    SettingsCleared,
    IdentityChanged,
}

/// <summary>
/// Immutable snapshot of the session that just ended. Callers must not mutate it.
/// </summary>
public sealed class AuthSessionEndedEventArgs : EventArgs
{
    public AuthSessionEndedEventArgs(AccountBinding oldBinding, AuthSessionEndReason reason)
    {
        OldBinding = oldBinding ?? throw new ArgumentNullException(nameof(oldBinding));
        Reason = reason;
    }

    public AccountBinding OldBinding { get; }
    public AuthSessionEndReason Reason { get; }
}

/// <summary>
/// Optional auth capability separate from <see cref="IAuthService"/>. Existing fakes stay compatible
/// without implementing this interface; production <c>AuthService</c> raises
/// <see cref="SessionEnded"/> only after explicit user operations complete.
/// </summary>
public interface IAuthSessionLifecycle
{
    event EventHandler<AuthSessionEndedEventArgs>? SessionEnded;
}
