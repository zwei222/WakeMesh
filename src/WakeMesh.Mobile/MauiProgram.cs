using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using WakeMesh.Client;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;
using WakeMesh.Mobile.Services.Push;
using WakeMesh.Mobile.ViewModels;
using WakeMesh.Mobile.Views;

namespace WakeMesh.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // 共通サービス登録（DIとBootReceiverで完全同一パスを使用、実登録コードを共有）
        builder.Services.AddWakeMeshCoreServices();
        builder.Services.AddWakeMeshAuthServices();
        builder.Services.AddSingleton<ICloudDeviceRegistrationStore, FileCloudDeviceRegistrationStore>();
        builder.Services.AddSingleton<ICloudManagementService, CloudManagementService>();
        builder.Services.AddSingleton<ICloudNavigation, MauiShellCloudNavigation>();
        builder.Services.AddSingleton<ICloudForegroundSyncService, CloudForegroundSyncService>();
        builder.Services.AddSingleton<IPushTokenStore, FilePushTokenStore>();

        // T118: read-only view of what has already been applied on this device, plus the in-memory hand-over
        // of the last really-received desired snapshot. Neither reserves an OS alarm nor writes a manifest.
        builder.Services.AddSingleton<DesiredObservationStore>();
        builder.Services.AddSingleton<IGroupAlarmSource, GroupAlarmSource>();

        // プラットフォームネイティブ実装の注入
#if ANDROID
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<TimePicker, Platforms.Android.WakeMeshTimePickerHandler>());
        builder.Services.AddSingleton<IAlarmScheduler, Platforms.Android.AndroidAlarmScheduler>();
        builder.Services.AddSingleton<IPermissionService, Platforms.Android.AndroidPermissionService>();
        builder.Services.AddSingleton<IAlarmRingingService, Platforms.Android.AndroidAlarmRingingService>();
        builder.Services.AddSingleton<IAudioPlaybackService, Platforms.Android.AndroidAudioPlaybackService>();
        builder.Services.AddSingleton<IMainThreadDispatcher, Platforms.Android.MauiMainThreadDispatcher>();
        builder.Services.AddSingleton<ISecureStorageProvider, Platforms.Android.MauiSecureStorageProvider>();
        // T196: ringing background theme (own SharedPreferences file, read synchronously by AlarmAlertActivity).
        builder.Services.AddSingleton<IRingingThemeStore, Platforms.Android.SharedPreferencesRingingThemeStore>();
        builder.Services.AddSingleton<IMsalClientAdapter>(sp =>
        {
            // Non-blocking instantiation: zero GetAwaiter().GetResult(), clientId updated asynchronously
            return new MsalClientAdapter();
        });
        builder.Services.AddSingleton<IPushNotificationAdapter, Platforms.Android.Push.AndroidPushNotificationAdapter>();
        builder.Services.AddSingleton<IWorkManagerAdapter, Platforms.Android.Push.AndroidWorkManagerAdapter>();
        builder.Services.AddSingleton<IPushManager>(sp =>
        {
            var isConfigured = Platforms.Android.Push.AndroidPushConfigurationProvider.IsFirebaseConfigured(global::Android.App.Application.Context);
            return new PushManager(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<ICloudDeviceRegistrationStore>(),
                sp.GetService<IDeviceCredentialStore>(),
                sp.GetRequiredService<IPushTokenStore>(),
                sp.GetRequiredService<IPushNotificationAdapter>(),
                sp.GetRequiredService<IWorkManagerAdapter>(),
                isConfigured: isConfigured,
                cloudManagementService: sp.GetService<ICloudManagementService>());
        });
#else
        builder.Services.AddSingleton<IAlarmScheduler, StubAlarmScheduler>();
        builder.Services.AddSingleton<IPermissionService, StubPermissionService>();
        builder.Services.AddSingleton<IAlarmRingingService, StubAlarmRingingService>();
        builder.Services.AddSingleton<IAudioPlaybackService, StubAudioPlaybackService>();
        builder.Services.AddSingleton<ISecureStorageProvider, StubSecureStorageProvider>();
        builder.Services.AddSingleton<IMsalClientAdapter, StubMsalClientAdapter>();
        builder.Services.AddSingleton<IPushNotificationAdapter, StubPushNotificationAdapter>();
        builder.Services.AddSingleton<IWorkManagerAdapter, StubWorkManagerAdapter>();
        builder.Services.AddSingleton<IPushManager>(sp => new PushManager(
            sp.GetRequiredService<IAuthService>(),
            sp.GetRequiredService<ICloudDeviceRegistrationStore>(),
            sp.GetService<IDeviceCredentialStore>(),
            sp.GetRequiredService<IPushTokenStore>(),
            sp.GetRequiredService<IPushNotificationAdapter>(),
            sp.GetRequiredService<IWorkManagerAdapter>(),
            isConfigured: true,
            cloudManagementService: sp.GetService<ICloudManagementService>()));
#endif

        // ViewModels
        builder.Services.AddTransient<AlarmListViewModel>();
        builder.Services.AddTransient<AlarmEditViewModel>();
        builder.Services.AddTransient<DiagnosticsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<CloudManagementViewModel>();
        builder.Services.AddTransient<CloudAlarmGroupEditViewModel>();

        // Views
        builder.Services.AddTransient<AlarmsPage>();
        builder.Services.AddTransient<AlarmEditPage>();
        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<CloudManagementPage>();
        builder.Services.AddTransient<CloudAlarmGroupEditPage>();

        var app = builder.Build();
        PushServiceResolver.CurrentServices = app.Services;
        return app;
    }
}
