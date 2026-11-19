namespace WakeMesh.Mobile.Services.Push;

public enum PushServiceStatus
{
    NotConfigured,
    Disabled,
    Idle,
    PendingAuthOrRegistration,
    Registered,
    RegistrationFailed,
    Unregistering,
    Unregistered,
    UnregistrationFailed
}
