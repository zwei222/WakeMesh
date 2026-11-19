// T73: CloudManagementViewModel now marshals UI-bound updates through
// WakeMesh.Mobile.Services.IMainThreadDispatcher. That contract file lives in
// src/WakeMesh.Mobile/Services/ and is not part of this test project's linked sources, so the
// same two-member contract is declared here for the net-only test compilation. The Android build
// binds to the real Services/IMainThreadDispatcher.cs (registered in MauiProgram), so this file
// never ships in the app and never changes production behavior.
using System;

namespace WakeMesh.Mobile.Services;

public interface IMainThreadDispatcher
{
    bool IsDispatchRequired { get; }

    void Post(Action action);
}

/// <summary>Analyzer/dead-code parity double for the inline dispatcher used as the view model default.</summary>
public sealed class ImmediateMainThreadDispatcher : IMainThreadDispatcher
{
    public bool IsDispatchRequired => false;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
