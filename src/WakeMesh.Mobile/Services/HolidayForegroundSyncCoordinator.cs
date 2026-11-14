using System;
using System.Threading;
using System.Threading.Tasks;

namespace WakeMesh.Mobile.Services;

public sealed class HolidayForegroundSyncCoordinator
{
    private readonly IHolidayService _holidayService;
    private readonly AlarmCoordinator _alarmCoordinator;
    private int _isRefreshing = 0;

    public HolidayForegroundSyncCoordinator(IHolidayService holidayService, AlarmCoordinator alarmCoordinator)
    {
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _alarmCoordinator = alarmCoordinator ?? throw new ArgumentNullException(nameof(alarmCoordinator));
    }

    /// <summary>
    /// Checks for initial missing data or stale cache (>30 days) and triggers background refresh
    /// without blocking the UI or broadcast receiver threads. Reschedules alarms ONLY on successful refresh.
    /// </summary>
    public void RequestForegroundSync()
    {
        if (!_holidayService.NeedsRefresh) return;
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var success = await _holidayService.RefreshHolidaysAsync();
                if (success)
                {
                    await _alarmCoordinator.RescheduleAllActiveAsync();
                }
            }
            catch
            {
                // Non-blocking background sync never throws to foreground
            }
            finally
            {
                Interlocked.Exchange(ref _isRefreshing, 0);
            }
        });
    }

    /// <summary>
    /// Synchronously awaits refresh and conditional rescheduling for tests.
    /// </summary>
    public async Task<bool> SyncNowAsync()
    {
        if (!_holidayService.NeedsRefresh) return false;
        var success = await _holidayService.RefreshHolidaysAsync();
        if (success)
        {
            await _alarmCoordinator.RescheduleAllActiveAsync();
        }
        return success;
    }
}
