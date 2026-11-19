using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Views;

namespace WakeMesh.Mobile;

public partial class AppShell : Shell
{
    private readonly ICloudForegroundSyncService? _foregroundSync;

    public AppShell() : this(null)
    {
    }

    public AppShell(ICloudForegroundSyncService? foregroundSync)
    {
        _foregroundSync = foregroundSync;
        InitializeComponent();
        Routing.RegisterRoute(nameof(AlarmEditPage), typeof(AlarmEditPage));
        Routing.RegisterRoute(nameof(CloudAlarmGroupEditPage), typeof(CloudAlarmGroupEditPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        AttachForegroundSync();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        AttachForegroundSync();
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        AttachForegroundSync();
    }

    private void AttachForegroundSync()
    {
        if (Window == null) return;
        var sync = _foregroundSync ?? Handler?.MauiContext?.Services?.GetService<ICloudForegroundSyncService>();
        sync?.AttachWindow(Window);
    }
}
