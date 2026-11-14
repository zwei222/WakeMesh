using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.Push;

namespace WakeMesh.Mobile;

public partial class App : Application
{
	private readonly IServiceProvider? _services;

	public App()
	{
		InitializeComponent();
	}

	public App(IServiceProvider services)
	{
		_services = services;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var foregroundSync = _services?.GetService<ICloudForegroundSyncService>();
		var shell = new AppShell(foregroundSync);
		var window = new Window(shell);

		window.Created += (_, _) =>
		{
			// Fire-and-forget startup init; Settings page also awaits the same idempotent gate.
			_ = InitializeStartupServicesAsync();
		};

		window.Resumed += (_, _) =>
		{
			_ = OnForegroundResumedAsync();
		};

		window.Activated += (_, _) =>
		{
			_ = OnForegroundResumedAsync();
		};

		return window;
	}

	private async Task InitializeStartupServicesAsync()
	{
		try
		{
			var auth = _services?.GetService<IAuthService>();
			if (auth != null)
			{
				await auth.InitializeAsync();
			}
		}
		catch
		{
			// Unconfigured / storage errors must not prevent local alarm startup.
		}

		try
		{
			var pushManager = _services?.GetService<IPushManager>();
			if (pushManager != null)
			{
				await pushManager.InitializeAsync();
			}

#if ANDROID
			var context = global::Android.App.Application.Context;
			var token = await Platforms.Android.Push.AndroidPushConfigurationProvider.TryFetchInitialFcmTokenAsync(context);
			if (!string.IsNullOrEmpty(token) && pushManager != null)
			{
				await pushManager.OnNewTokenReceivedAsync(token);
			}
#endif
		}
		catch
		{
			// Safe containment
		}
	}

	private async Task OnForegroundResumedAsync()
	{
		try
		{
			var pushManager = _services?.GetService<IPushManager>();
			if (pushManager != null)
			{
				await pushManager.InitializeAsync();
				await pushManager.OnNotificationPermissionChangedAsync();
			}
		}
		catch
		{
			// Safe containment
		}
	}
}
