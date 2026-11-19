using System;
using System.Threading.Tasks;
using WakeMesh.Mobile.Services;

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
/// T251: the boundary takes the main-thread dispatcher so every caller path (including the alarm-list
/// command, which reaches it after a pool continuation) is safe without changing any call site.
/// </summary>
public sealed class MauiShellCloudNavigation : ICloudNavigation
{
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Func<string, Task> _navigate;

    /// <summary>Production constructor: the DI container supplies the same dispatcher the app uses.</summary>
    public MauiShellCloudNavigation(IMainThreadDispatcher dispatcher)
        : this(dispatcher, null)
    {
    }

    /// <summary>
    /// Defensive fallback for the cloud view models' "no navigation was injected" branch. Internal so the
    /// only public constructor is the DI one.
    /// </summary>
    internal MauiShellCloudNavigation()
        : this(new ImmediateMainThreadDispatcher(), null)
    {
    }

    /// <summary>
    /// Test-only seam for the unavoidable native boundary (Shell is not constructible off-device). The
    /// delegate replaces <see cref="NavigateWithShellAsync"/> only; validation, dispatching and completion
    /// stay under test.
    /// </summary>
    internal MauiShellCloudNavigation(IMainThreadDispatcher dispatcher, Func<string, Task>? navigate)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _navigate = navigate ?? NavigateWithShellAsync;
    }

    public async Task GoToAsync(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        // Already on the UI thread (cloud-tab entry): enter the transition directly, no extra hop.
        if (!_dispatcher.IsDispatchRequired)
        {
            await _navigate(route).ConfigureAwait(false);
            return;
        }

        // Alarm-list entry arrives here on a pool continuation: the boundary - not the caller - restores
        // the UI thread and reports completion only when the native transition itself has finished.
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.Post(() => { _ = NavigateOnDispatcherAsync(route, completion); });
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the transition on the dispatched thread. Everything is caught into the completion source, so
    /// the discarded task can never fault and there is no unobserved async-void path.
    /// </summary>
    private async Task NavigateOnDispatcherAsync(string route, TaskCompletionSource<object?> completion)
    {
        try
        {
            await _navigate(route).ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// The typed native call. <see cref="Shell.Current"/> is read here, on the thread that finally runs
    /// the transition, never before the dispatch.
    /// </summary>
    private static async Task NavigateWithShellAsync(string route)
    {
#if ANDROID || IOS || MACCATALYST || WINDOWS
        var shell = Shell.Current;
        if (shell is not null)
        {
            await shell.GoToAsync((ShellNavigationState)route);
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
