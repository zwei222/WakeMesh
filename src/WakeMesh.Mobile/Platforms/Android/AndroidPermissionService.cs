using System;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Platforms.Android;

public sealed class AndroidPermissionService : IPermissionService
{
    private readonly Context _context;

    public AndroidPermissionService(Context? context = null)
    {
        _context = context ?? global::Android.App.Application.Context;
    }

    public int OsApiLevel => (int)Build.VERSION.SdkInt;

    public bool CanScheduleExactAlarms
    {
        get
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                var alarmManager = (AlarmManager?)_context.GetSystemService(Context.AlarmService);
                return alarmManager?.CanScheduleExactAlarms() ?? false;
            }
            return true;
        }
    }

    public bool HasNotificationPermission
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                return ContextCompat.CheckSelfPermission(_context, Manifest.Permission.PostNotifications) == Permission.Granted;
            }
            var compat = NotificationManagerCompat.From(_context);
            return compat?.AreNotificationsEnabled() ?? true;
        }
    }

    public bool IsIgnoringBatteryOptimizations
    {
        get
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var powerManager = (PowerManager?)_context.GetSystemService(Context.PowerService);
                return powerManager?.IsIgnoringBatteryOptimizations(_context.PackageName) ?? false;
            }
            return true;
        }
    }

    public bool CanUseFullScreenIntent
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                var notificationManager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
                return notificationManager?.CanUseFullScreenIntent() ?? true;
            }
            return true;
        }
    }

    // Item 4: POST_NOTIFICATIONS runtime request
    public void RequestNotificationPermission()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                ActivityCompat.RequestPermissions(
                    activity,
                    new[] { Manifest.Permission.PostNotifications },
                    1010);
            }
        }
    }

    // Item 4: Exact alarm permission guidance & settings navigation
    public void OpenExactAlarmSettings()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                var intent = new Intent(Settings.ActionRequestScheduleExactAlarm);
                intent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
                intent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(intent);
                return;
            }
        }
        catch (Exception)
        {
            // Fallback to application details settings
        }

        try
        {
            var appSettingsIntent = new Intent(Settings.ActionApplicationDetailsSettings);
            appSettingsIntent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
            appSettingsIntent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(appSettingsIntent);
        }
        catch (Exception) { }
    }

    // T70: Doze / battery-optimization exemption entry point.
    // When the app is still optimized, request the package-scoped dialog
    // (ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS + package: URI) so the user lands on a screen
    // where this exact app can be toggled. If the app is already exempt, or the OS/vendor build does
    // not handle the request intent, open the reachable settings surface instead of doing nothing.
    public void OpenBatteryOptimizationSettings()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !IsIgnoringBatteryOptimizations)
        {
            try
            {
                var requestIntent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
                requestIntent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
                requestIntent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(requestIntent);
                return;
            }
            catch (Exception)
            {
                // Fall through to the list screen below.
            }
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            try
            {
                var listIntent = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                listIntent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(listIntent);
                return;
            }
            catch (Exception) { }
        }

        try
        {
            var appSettingsIntent = new Intent(Settings.ActionApplicationDetailsSettings);
            appSettingsIntent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
            appSettingsIntent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(appSettingsIntent);
        }
        catch (Exception) { }
    }

    public void OpenFullScreenIntentSettings()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                var intent = new Intent(Settings.ActionManageAppUseFullScreenIntent);
                intent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
                intent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(intent);
                return;
            }
        }
        catch (Exception) { }

        try
        {
            var appSettingsIntent = new Intent(Settings.ActionApplicationDetailsSettings);
            appSettingsIntent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
            appSettingsIntent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(appSettingsIntent);
        }
        catch (Exception) { }
    }
}
