#if ANDROID
using System;
using Microsoft.Maui.ApplicationModel;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class MauiMainThreadDispatcher : IMainThreadDispatcher
{
    public bool IsDispatchRequired => !MainThread.IsMainThread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (MainThread.IsMainThread)
        {
            action();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(action);
        }
    }
}
#endif
