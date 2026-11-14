using System;
using System.Threading.Tasks;

#if ANDROID || IOS || MACCATALYST || WINDOWS
using Microsoft.Maui.Controls;
#endif

namespace WakeMesh.Mobile.Services.CloudManagement;

/// <summary>
/// Typed abstraction for Shell navigation in cloud UI.
/// Replaces runtime reflection on Shell.GoToAsync.
/// </summary>
public interface ICloudNavigation
{
    Task GoToAsync(string route);
}

/// <summary>
/// Production navigation implementation wiring directly to Shell.GoToAsync(ShellNavigationState).
/// </summary>
public sealed class MauiShellCloudNavigation : ICloudNavigation
{
    public async Task GoToAsync(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
#if ANDROID || IOS || MACCATALYST || WINDOWS
        if (Shell.Current != null)
        {
            await Shell.Current.GoToAsync((ShellNavigationState)route).ConfigureAwait(false);
        }
#else
        await Task.CompletedTask;
#endif
    }
}

/// <summary>
/// Test double navigation adapter executing an action delegate.
/// </summary>
public sealed class ActionCloudNavigation : ICloudNavigation
{
    private readonly Action<string> _handler;

    public ActionCloudNavigation(Action<string> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task GoToAsync(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        _handler(route);
        return Task.CompletedTask;
    }
}
