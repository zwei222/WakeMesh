using Microsoft.Extensions.DependencyInjection;
using WakeMesh.Client;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public static class WakeMeshServiceCollectionExtensions
{
    public static IServiceCollection AddWakeMeshCoreServices(this IServiceCollection services, string? alarmsPath = null, string? deviceIdPath = null)
    {
        services.AddSingleton<IAlarmStore>(_ => new JsonFileAlarmStore(alarmsPath ?? AlarmStoragePathHelper.GetAlarmsJsonPath()));
        services.AddSingleton<IHolidayService>(_ => new JapaneseHolidayService());
        services.AddSingleton<AlarmScheduleCalculator>();
        services.AddSingleton<IDeviceInfoProvider>(_ => new LocalDeviceInfoProvider(storageFilePath: deviceIdPath ?? AlarmStoragePathHelper.GetDeviceIdFilePath()));
        services.AddSingleton<AlarmCoordinator>();
        services.AddSingleton<HolidayForegroundSyncCoordinator>();
        services.AddSingleton<IMainThreadDispatcher, ImmediateMainThreadDispatcher>();
        return services;
    }

    public static IServiceCollection AddWakeMeshAuthServices(
        this IServiceCollection services,
        string? authSettingsPath = null)
    {
        services.AddSingleton<IAuthSettingsStore>(_ => new JsonAuthSettingsStore(authSettingsPath ?? AlarmStoragePathHelper.GetAuthSettingsFilePath()));
        services.AddSingleton<IDeviceCredentialStore>(sp =>
        {
            var secureStorage = sp.GetRequiredService<ISecureStorageProvider>();
            return new SecureDeviceCredentialStore(secureStorage, () => sp.GetRequiredService<IAuthService>().CurrentBinding);
        });
        services.AddSingleton<IAuthService>(sp =>
        {
            var store = sp.GetRequiredService<IAuthSettingsStore>();
            var msal = sp.GetRequiredService<IMsalClientAdapter>();
            var cred = sp.GetService<IDeviceCredentialStore>();
            var dev = sp.GetService<IDeviceInfoProvider>();
            return new AuthService(store, msal, initialSettings: null, httpClient: null, credentialStore: cred, deviceInfoProvider: dev);
        });
        services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<IAuthService>());
        return services;
    }
}
