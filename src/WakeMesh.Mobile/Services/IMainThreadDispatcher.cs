using System;

namespace WakeMesh.Mobile.Services;

public interface IMainThreadDispatcher
{
    bool IsDispatchRequired { get; }
    void Post(Action action);
}

public sealed class ImmediateMainThreadDispatcher : IMainThreadDispatcher
{
    public bool IsDispatchRequired => false;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
