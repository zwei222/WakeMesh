using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.Services.CloudSync;

namespace WakeMesh.Mobile.ViewModels;

public sealed class AlarmListViewModel : ViewModelBase, IDisposable
{
    private readonly AlarmCoordinator _coordinator;
    private readonly IHolidayService _holidayService;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IAlarmRingingService? _ringingService;
    private readonly HolidayForegroundSyncCoordinator? _holidaySyncCoordinator;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IPermissionService? _permissionService;
    private readonly ICloudManagementService? _cloud;
    private readonly DesiredObservationStore? _observations;
    private readonly IGroupAlarmSource? _groupSource;
    private readonly ICloudNavigation? _navigation;

    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private readonly HashSet<string> _cloudManagedLocalIds = new(StringComparer.Ordinal);

    private bool _isBusy;
    private bool _isRingingSubscribed;
    private bool _isSyncSubscribed;
    private bool _disposed;
    private int _projectionDirty;
    private Task _pendingProjection = Task.CompletedTask;
    private string? _statusMessage;
    private LocalAlarmModel? _selectedAlarm;
    private string _readinessLevel = "None";
    private string _readinessText = string.Empty;

    public ObservableCollection<LocalAlarmModel> Alarms { get; } = new();
    public ObservableCollection<ActiveRingingAlarm> RingingAlarms { get; } = new();
    public ObservableCollection<GroupAlarmRow> GroupAlarms { get; } = new();

    public bool IsRinging => RingingAlarms.Count > 0;

    /// <summary>T113: true while at least one cloud/group row is listed under the manual alarms.</summary>
    public bool HasGroupAlarms => GroupAlarms.Count > 0;

    /// <summary>T113 §3: fixed section heading for the group rows.</summary>
    public string GroupSectionCaption => "グループのアラーム";

    /// <summary>
    /// EmptyView title. When every alarm on this device comes from the cloud the manual list is empty but
    /// the page is not: say why instead of claiming there are no alarms.
    /// </summary>
    public string EmptyTitle => Alarms.Count == 0 && HasGroupAlarms
        ? "この端末で作ったアラームはありません"
        : "アラームはまだありません";

    /// <summary>Read-only: only a configured and signed-in cloud can open the group editor.</summary>
    public bool CanEditCloudGroups => _cloud?.IsConfiguredAndSignedIn ?? false;

    public string RingingBannerTitle => RingingAlarms.Count switch
    {
        0 => string.Empty,
        1 => $"{RingingAlarms[0].AlarmName} のアラームが鳴っています",
        _ => $"{RingingAlarms.Count} 件のアラームが同時に鳴っています"
    };

    /// <summary>
    /// T80 P0-8: the alarm tab states whether this device can actually ring, without repeating the
    /// diagnostics page. "None" means every permission this OS level requires is known and granted;
    /// "Critical" means exact alarms are needed but not granted; "Warn" covers the remaining gaps.
    /// </summary>
    public string ReadinessLevel => _readinessLevel;

    /// <summary>Plain-language list of what is missing. Empty when <see cref="ReadinessLevel"/> is None.</summary>
    public string ReadinessText => _readinessText;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                LoadAlarmsCommand.RaiseCanExecuteChanged();
                ToggleAlarmCommand.RaiseCanExecuteChanged();
                DeleteAlarmCommand.RaiseCanExecuteChanged();
                DismissRingingAlarmCommand.RaiseCanExecuteChanged();
                DismissAllRingingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public LocalAlarmModel? SelectedAlarm
    {
        get => _selectedAlarm;
        set => SetProperty(ref _selectedAlarm, value);
    }

    // 正直なステータス表示（偽装しない）
    public string CloudSyncStatusText => "未接続 (ローカル動作中)";
    public string AuthStatusText => "未サインイン";
    public string DeviceDisplayName => _deviceInfoProvider.DeviceDisplayName;
    public string DeviceId => _deviceInfoProvider.DeviceId;

    // T88 copy-map: the row states the fact in plain words. The service's internal warning text
    // ("祝日データ未取得 (鳴動優先)") stays on the settings page as the refresh detail, not here.
    public string HolidayStatusText => _holidayService.HasHolidayData
        ? "内蔵の祝日データを確認済み（祝日はアラームが鳴りません）"
        : "祝日情報が未取得です（祝日もアラーム対象）";

    public string HolidayNotice => HolidayStatusText;

    /// <summary>T80 P1-2: true while the holiday calendar is unknown, so holidays still ring.</summary>
    public bool IsHolidayDataMissing => !_holidayService.HasHolidayData;

    public AsyncRelayCommand LoadAlarmsCommand { get; }
    public AsyncRelayCommand ToggleAlarmCommand { get; }
    public AsyncRelayCommand DeleteAlarmCommand { get; }
    public AsyncRelayCommand DismissRingingAlarmCommand { get; }
    public AsyncRelayCommand DismissAllRingingCommand { get; }
    public AsyncRelayCommand OpenGroupCommand { get; }

    public AlarmListViewModel(
        AlarmCoordinator coordinator,
        IHolidayService holidayService,
        IDeviceInfoProvider deviceInfoProvider,
        IAlarmRingingService? ringingService = null,
        HolidayForegroundSyncCoordinator? holidaySyncCoordinator = null,
        IMainThreadDispatcher? dispatcher = null,
        IPermissionService? permissionService = null,
        ICloudManagementService? cloud = null,
        DesiredObservationStore? observations = null,
        IGroupAlarmSource? groupSource = null,
        ICloudNavigation? navigation = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _holidayService = holidayService ?? throw new ArgumentNullException(nameof(holidayService));
        _deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
        _ringingService = ringingService;
        _holidaySyncCoordinator = holidaySyncCoordinator;
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();
        _permissionService = permissionService;
        _cloud = cloud;
        _observations = observations;
        _groupSource = groupSource;
        _navigation = navigation;

        LoadAlarmsCommand = new AsyncRelayCommand(async _ => await LoadAlarmsAsync(), _ => !IsBusy);
        ToggleAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await ToggleAlarmAsync(id);
            }
            else if (param is LocalAlarmModel alarm)
            {
                await ToggleAlarmAsync(alarm);
            }
        }, _ => !IsBusy);
        DeleteAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await DeleteAlarmAsync(id);
            }
            else if (param is LocalAlarmModel alarm)
            {
                await DeleteAlarmAsync(alarm);
            }
        }, _ => !IsBusy);

        DismissRingingAlarmCommand = new AsyncRelayCommand(async param =>
        {
            if (param is string id)
            {
                await DismissRingingAlarmAsync(id);
            }
            else if (param is ActiveRingingAlarm ringing)
            {
                await DismissRingingAlarmAsync(ringing.AlarmId);
            }
        }, _ => true); // T80 P0-5: stopping must not wait for the list reload

        DismissAllRingingCommand = new AsyncRelayCommand(async _ =>
        {
            await DismissAllRingingAsync();
        }, _ => IsRinging); // T80 P0-5: stopping must not wait for the list reload

        // T113 §3: a group row never opens the manual editor. It only asks the existing cloud route to
        // show that group, and only when the row carries a group id and the cloud is signed in.
        // T187R: the row's GroupId is only the gate (the producer is frozen). The id handed to the
        // editor is resolved from the live server list, because the manifest copy can be stale - a stale
        // id would open nothing (and could previously fall through to the new-group defaults). Read-only:
        // it lists groups and navigates, nothing else.
        OpenGroupCommand = new AsyncRelayCommand(async param =>
        {
            if (param is not GroupAlarmRow row || !CanOpenGroupRow(row))
            {
                return;
            }

            var navigation = _navigation;
            if (navigation is null)
            {
                return;
            }

            try
            {
                if (row.ScheduleId is not Guid scheduleId || scheduleId == Guid.Empty || _cloud is null)
                {
                    await RunOnUiThreadAsync(() => StatusMessage = GroupOpenFailureMessage).ConfigureAwait(false);
                    return;
                }

                await RunOnUiThreadAsync(() => StatusMessage = GroupOpenLoadingMessage).ConfigureAwait(false);

                var groupsResult = await _cloud.ListAlarmGroupsAsync().ConfigureAwait(false);
                if (!groupsResult.IsSuccess || groupsResult.Value is null)
                {
                    await RunOnUiThreadAsync(() => StatusMessage = GroupOpenFailureMessage).ConfigureAwait(false);
                    return;
                }

                var groupId = ResolveOwnerGroupId(groupsResult.Value, scheduleId);
                if (groupId is null)
                {
                    await RunOnUiThreadAsync(() => StatusMessage = GroupOpenFailureMessage).ConfigureAwait(false);
                    return;
                }

                // The resolve succeeded: drop the progress text back to the normal footer wording.
                await RunOnUiThreadAsync(() => StatusMessage = BuildStatusMessage()).ConfigureAwait(false);

                await navigation
                    .GoToAsync($"CloudAlarmGroupEditPage?groupId={Uri.EscapeDataString(groupId)}")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await RunOnUiThreadAsync(() => StatusMessage = "グループの画面を開けませんでした").ConfigureAwait(false);
            }
        }, param => param is GroupAlarmRow row && CanOpenGroupRow(row));

        RefreshReadiness();
        SubscribeRingingEvents();
    }

    private bool CanOpenGroupRow(GroupAlarmRow row) =>
        row is not null &&
        row.CanOpenGroup &&
        !string.IsNullOrEmpty(row.GroupId) &&
        CanEditCloudGroups;

    /// <summary>Fixed text for every "cannot resolve the owner group" case; never a route with the row id.</summary>
    private const string GroupOpenFailureMessage = "グループを特定できませんでした。一覧を更新してください";

    /// <summary>Shown in the existing footer while the read-only resolve call is in flight.</summary>
    private const string GroupOpenLoadingMessage = "グループを開いています...";

    /// <summary>
    /// T187R: the single owner of the schedule. <c>ScheduleId</c> is a server target id
    /// (<c>DeviceSchedule.Id</c>), the same value the group DTO carries on its targets, and each schedule
    /// row has exactly one owning group - so an exact <see cref="Guid"/> match on one live group is the
    /// only sound answer. Anything else (none, or more than one) is unresolved: no label/id repair, no
    /// fallback to the row's own GroupId.
    /// </summary>
    private static string? ResolveOwnerGroupId(IReadOnlyList<AlarmGroupDto> groups, Guid scheduleId)
    {
        List<string> matches = new(2);

        foreach (var group in groups)
        {
            if (group is null ||
                group.DeletedAt is not null ||
                string.IsNullOrWhiteSpace(group.Id) ||
                group.Targets is null)
            {
                continue;
            }

            var owns = false;
            foreach (var target in group.Targets)
            {
                if (target is not null && target.Id == scheduleId)
                {
                    owns = true;
                    break;
                }
            }

            if (!owns)
            {
                continue;
            }

            var alreadySeen = false;
            foreach (var existing in matches)
            {
                if (string.Equals(existing, group.Id, StringComparison.Ordinal))
                {
                    alreadySeen = true;
                    break;
                }
            }

            if (!alreadySeen)
            {
                matches.Add(group.Id);
                if (matches.Count > 1)
                {
                    return null;   // ambiguous: refuse instead of guessing
                }
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    public void SubscribeRingingEvents()
    {
        if (_ringingService == null || _isRingingSubscribed) return;
        _ringingService.RingingStateChanged += OnRingingStateChanged;
        _isRingingSubscribed = true;
        DispatchSyncRingingAlarms();
    }

    public void UnsubscribeRingingEvents()
    {
        if (_ringingService == null || !_isRingingSubscribed) return;
        _ringingService.RingingStateChanged -= OnRingingStateChanged;
        _isRingingSubscribed = false;
    }

    private void OnRingingStateChanged(object? sender, EventArgs e)
    {
        DispatchSyncRingingAlarms();
    }

    /// <summary>
    /// T113 §5: the list follows the sync outcome and the last observation. These callbacks only re-project
    /// (read store + manifest, replace changed rows); they never reschedule an OS alarm and never touch
    /// <see cref="IsBusy"/>, so the RefreshView spinner cannot start spinning every 15 seconds.
    /// </summary>
    public void SubscribeSyncEvents()
    {
        if (_isSyncSubscribed)
        {
            return;
        }

        if (_cloud is not null)
        {
            _cloud.SyncCompleted += OnSyncSourceChanged;
        }

        if (_observations is not null)
        {
            _observations.Changed += OnSyncSourceChanged;
        }

        _isSyncSubscribed = _cloud is not null || _observations is not null;

        if (_observations?.Latest is not null)
        {
            ScheduleProjectionRefresh();
        }
    }

    public void UnsubscribeSyncEvents()
    {
        if (!_isSyncSubscribed)
        {
            return;
        }

        if (_cloud is not null)
        {
            _cloud.SyncCompleted -= OnSyncSourceChanged;
        }

        if (_observations is not null)
        {
            _observations.Changed -= OnSyncSourceChanged;
        }

        _isSyncSubscribed = false;
    }

    private void OnSyncSourceChanged(object? sender, EventArgs e) => ScheduleProjectionRefresh();

    private void DispatchSyncRingingAlarms()
    {
        if (_dispatcher.IsDispatchRequired)
        {
            _dispatcher.Post(SyncRingingAlarms);
        }
        else
        {
            SyncRingingAlarms();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        UnsubscribeRingingEvents();
        UnsubscribeSyncEvents();
    }

    private void SyncRingingAlarms()
    {
        if (_ringingService == null) return;

        var active = _ringingService.GetRingingAlarms();
        RingingAlarms.Clear();
        foreach (var item in active)
        {
            RingingAlarms.Add(item);
        }

        OnPropertyChanged(nameof(IsRinging));
        OnPropertyChanged(nameof(RingingBannerTitle));
        DismissAllRingingCommand.RaiseCanExecuteChanged();
    }

    public async Task<bool> DismissRingingAlarmAsync(string alarmId)
    {
        if (_ringingService == null || string.IsNullOrWhiteSpace(alarmId)) return false;

        var success = await _ringingService.DismissAlarmAsync(alarmId);
        SyncRingingAlarms();
        return success;
    }

    public async Task<bool> DismissAllRingingAsync()
    {
        if (_ringingService == null) return false;

        var success = await _ringingService.DismissAllAsync();
        SyncRingingAlarms();
        return success;
    }

    public async Task LoadAlarmsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "アラームを読み込み中...";

        try
        {
            await Task.Yield();
            await _coordinator.RescheduleAllActiveAsync();
            // T113 §5: the load owns the projection, so it may run while IsBusy and consumes any dirty flag
            // a sync notification raised meanwhile.
            // T123 M2: no ConfigureAwait(false) here — the ringing banner, the spinner release and the
            // readiness text below are UI state and must keep the UI SynchronizationContext continuation.
            await RefreshProjectionAsync(allowWhileBusy: true);
            SyncRingingAlarms();
            _holidaySyncCoordinator?.RequestForegroundSync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"読込失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            // Exactly one catch-up run for every notification that arrived while busy.
            await RunDeferredProjectionCatchUpAsync();
            RefreshReadiness();
        }
    }

    /// <summary>
    /// T123 m2: a sync notification that arrived while <see cref="IsBusy"/> only set the dirty flag. The
    /// operation that owned the busy state runs this once on the UI thread when it finishes, so the list
    /// reflects the notification without waiting for the next sync cycle. Read-only: never reschedules.
    /// </summary>
    private async Task RunDeferredProjectionCatchUpAsync()
    {
        if (Volatile.Read(ref _projectionDirty) == 0)
        {
            return;
        }

        try
        {
            await ReadOnlyRefreshProjectionAsync();
        }
        catch (Exception)
        {
            StatusMessage = ProjectionFailureMessage;
        }
    }

    private const string ProjectionFailureMessage = "グループのアラームを更新できませんでした";

    /// <summary>The projection task most recently scheduled from a sync notification (tests await this).</summary>
    public Task WaitForPendingProjectionAsync() => _pendingProjection;

    /// <summary>
    /// Re-projects the list without touching the OS scheduler or <see cref="IsBusy"/>. Concurrent calls
    /// coalesce: a call that arrives while another run is in progress just marks the projection dirty and
    /// the running loop does one more pass with the newest state.
    /// </summary>
    public Task ReadOnlyRefreshProjectionAsync() => RefreshProjectionAsync(allowWhileBusy: false);

    private async Task RefreshProjectionAsync(bool allowWhileBusy)
    {
        if (allowWhileBusy)
        {
            // T123 m1: the manual load must not skip its projection. A notification projection may already
            // hold the gate; wait for it instead of returning stale rows. Clearing dirty only once the gate
            // is held keeps a notification that arrives later from being swallowed.
            await _projectionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _projectionDirty, 0);
                await ProjectOnceAsync().ConfigureAwait(false);
            }
            finally
            {
                _projectionGate.Release();
            }

            return;
        }

        Interlocked.Exchange(ref _projectionDirty, 1);

        if (!await _projectionGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;   // a run is already in progress; it will pick up the dirty flag
        }

        try
        {
            while (true)
            {
                if (IsBusy)
                {
                    // Deferred: the load / mutation that owns IsBusy runs one catch-up afterwards.
                    Interlocked.Exchange(ref _projectionDirty, 1);
                    return;
                }

                if (Interlocked.Exchange(ref _projectionDirty, 0) == 0)
                {
                    return;
                }

                await ProjectOnceAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    private async Task ProjectOnceAsync()
    {
        var stored = await _coordinator.GetAllAsync().ConfigureAwait(false);

        GroupAlarmSourceResult source = GroupAlarmSourceResult.None;
        if (_groupSource is not null)
        {
            source = await _groupSource.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var projection = GroupAlarmProjector.Project(
            stored,
            source.Binding,
            source.Manifest,
            _observations?.Latest,
            TimeZoneInfo.Local,
            source.ManifestUnreadable);

        await RunOnUiThreadAsync(() => ApplyProjection(projection)).ConfigureAwait(false);
    }

    private void ApplyProjection(GroupAlarmProjection projection)
    {
        DiffCollection(Alarms, projection.LocalAlarms, alarm => alarm.Id, ManualAlarmsEqual);
        DiffCollection(GroupAlarms, projection.GroupAlarms, row => row.RowKey, (left, right) => Equals(left, right));

        _cloudManagedLocalIds.Clear();
        foreach (var row in projection.GroupAlarms)
        {
            if (!string.IsNullOrEmpty(row.LocalAlarmId))
            {
                _cloudManagedLocalIds.Add(row.LocalAlarmId);
            }
        }

        OnPropertyChanged(nameof(HasGroupAlarms));
        OnPropertyChanged(nameof(EmptyTitle));
        StatusMessage = BuildStatusMessage();
    }

    /// <summary>
    /// Minimal, key-based diff so an unchanged row is not replaced (no redundant redraw / re-bind) while a
    /// changed value still redraws exactly that row.
    /// </summary>
    private static void DiffCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, string> keySelector,
        Func<T, T, bool> equals)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var key = keySelector(target[i]);
            if (!desired.Any(item => string.Equals(keySelector(item), key, StringComparison.Ordinal)))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var key = keySelector(item);

            var found = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(keySelector(target[j]), key, StringComparison.Ordinal))
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                target.Insert(i, item);
                continue;
            }

            if (found != i)
            {
                target.Move(found, i);
            }

            if (!equals(target[i], item))
            {
                target[i] = item;
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static bool ManualAlarmsEqual(LocalAlarmModel left, LocalAlarmModel right) =>
        left.Id == right.Id &&
        left.Name == right.Name &&
        left.IsEnabled == right.IsEnabled &&
        left.Time == right.Time &&
        left.RepeatKind == right.RepeatKind &&
        left.ActiveWeekdays == right.ActiveWeekdays &&
        left.OnceOnDate == right.OnceOnDate &&
        left.ExcludeHolidays == right.ExcludeHolidays &&
        left.SnoozeMinutes == right.SnoozeMinutes &&
        left.SnoozeUntil == right.SnoozeUntil &&
        left.DeviceId == right.DeviceId &&
        left.TimeZoneId == right.TimeZoneId &&
        left.SyncStatus == right.SyncStatus &&
        left.OsRegistrationStatus == right.OsRegistrationStatus &&
        left.OsRegistrationError == right.OsRegistrationError &&
        left.VolumePercent == right.VolumePercent &&
        left.EnableVibration == right.EnableVibration &&
        left.SoundUri == right.SoundUri &&
        left.NextFireTime == right.NextFireTime &&
        left.HasHolidayCoverageWarning == right.HasHolidayCoverageWarning;

    /// <summary>
    /// Footer text. The group-free wording is byte-identical to the pre-T113 format (T109 asserts it); a
    /// present group section only appends a suffix, and the enabled count is now the manual one.
    /// </summary>
    private string BuildStatusMessage()
    {
        var manualCount = Alarms.Count;
        var enabledCount = Alarms.Count(alarm => alarm.IsEnabled);
        var text = $"{manualCount} 件のアラーム ({enabledCount} 件有効)";
        return HasGroupAlarms ? $"{text}・グループのアラーム {GroupAlarms.Count} 件" : text;
    }

    private void ScheduleProjectionRefresh()
    {
        if (_disposed)
        {
            return;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingProjection = completion.Task;

        void Run()
        {
            _ = RefreshAfterSyncEventAsync(completion);
        }

        if (_dispatcher.IsDispatchRequired)
        {
            _dispatcher.Post(Run);
        }
        else
        {
            Run();
        }
    }

    private async Task RefreshAfterSyncEventAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            await ReadOnlyRefreshProjectionAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never swallow a background failure silently: surface a safe message the footer can show.
            try
            {
                await RunOnUiThreadAsync(() => StatusMessage = ProjectionFailureMessage).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The dispatcher itself is gone (page torn down); there is nothing left to notify.
            }
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (!_dispatcher.IsDispatchRequired)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// T80 P0-8 / P1-2: re-reads the OS permission state. With no permission service the readiness is
    /// reported as unverified ("Warn") — it must never claim every permission is granted.
    /// </summary>
    public void RefreshReadiness()
    {
        var (level, text) = EvaluateReadiness();

        if (!string.Equals(_readinessLevel, level, StringComparison.Ordinal))
        {
            _readinessLevel = level;
            OnPropertyChanged(nameof(ReadinessLevel));
        }

        if (!string.Equals(_readinessText, text, StringComparison.Ordinal))
        {
            _readinessText = text;
            OnPropertyChanged(nameof(ReadinessText));
        }

        OnPropertyChanged(nameof(IsHolidayDataMissing));
    }

    private (string Level, string Text) EvaluateReadiness()
    {
        var permissions = _permissionService;
        if (permissions is null)
        {
            return ("Warn", "この端末の権限の状態を確認できませんでした。設定から確認してください。");
        }

        var api = permissions.OsApiLevel;
        if (api >= 31 && !permissions.CanScheduleExactAlarms)
        {
            return ("Critical", "「正確なアラーム」が許可されていません。");
        }

        var missing = new System.Collections.Generic.List<string>();
        if (api >= 33 && !permissions.HasNotificationPermission) missing.Add("通知権限");
        if (api >= 34 && !permissions.CanUseFullScreenIntent) missing.Add("全画面表示");
        if (!permissions.IsIgnoringBatteryOptimizations) missing.Add("電池最適化の除外");

        if (missing.Count == 0)
        {
            return ("None", string.Empty);
        }

        return ("Warn", $"アラームを鳴らすには、次の設定を確認してください。{string.Join("、", missing)}。");
    }

    public async Task<bool> ToggleAlarmAsync(LocalAlarmModel alarm)
    {
        if (alarm is null) return false;
        return await ToggleAlarmAsync(alarm.Id);
    }

    public async Task<bool> ToggleAlarmAsync(string id)
    {
        // T123 M1: the busy check and the busy claim happen in one block before the first await, so a second
        // tap cannot slip through the guard while the cloud-managed lookup is still pending. The await below
        // keeps the UI context (no ConfigureAwait(false)): the observable changes belong to the UI thread.
        if (IsBusy) return false;
        IsBusy = true;

        try
        {
            if (await IsCloudManagedAsync(id)) return false;   // T113 §3: cloud rows are read-only here

            var updated = await _coordinator.ToggleAlarmEnabledAsync(id);
            if (updated is not null)
            {
                var existing = Alarms.FirstOrDefault(a => a.Id == id);
                if (existing is not null)
                {
                    existing.IsEnabled = updated.IsEnabled;
                    existing.NextFireTime = updated.NextFireTime;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"切替エラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RunDeferredProjectionCatchUpAsync();
        }
        return false;
    }

    /// <summary>
    /// T105: applies an explicit ON/OFF chosen by the user. The plain toggle on this page was an
    /// inverting API, so a Switch re-binding (false→true on first bind, cell reuse, reload) used to
    /// flip the saved value. The requested value is honoured against the *stored* value, and equal
    /// values are a no-op. The coordinator's inverting toggle is only used when the store disagrees.
    /// </summary>
    public async Task<bool> SetAlarmEnabledFromUserAsync(LocalAlarmModel alarm, bool requested)
    {
        if (alarm is null || alarm.IsEnabled == requested) return false;   // Binding が値を反映しただけ → no-op
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            if (await IsCloudManagedAsync(alarm.Id)) return false;   // クラウド由来は端末側で変更しない

            var current = await _coordinator.GetByIdAsync(alarm.Id);
            if (current is null) return false;
            var result = current.IsEnabled == requested ? current
                       : await _coordinator.ToggleAlarmEnabledAsync(alarm.Id);   // 保存値が requested と違うときだけ反転
            if (result is null) return false;

            // T109 (T107 M1): LocalAlarmModel is a plain POCO with no INotifyPropertyChanged, so
            // writing IsEnabled/NextFireTime on the row object never re-renders it — an alarm switched
            // OFF kept showing "次回 …" and the stale enabled count (t106-native user_off).
            // Replace the item in the collection instead: ObservableCollection raises Replace, the row
            // re-binds to the saved values (no "次回" once disabled), and the re-bind feeds back the
            // saved value, which the equality no-op at the top of this method absorbs.
            var existing = Alarms.FirstOrDefault(a => a.Id == alarm.Id) ?? alarm;
            var updated = CopySavedState(result);
            var idx = Alarms.IndexOf(existing);
            if (idx >= 0)
            {
                Alarms[idx] = updated;
            }
            else
            {
                existing.IsEnabled = updated.IsEnabled;
                existing.NextFireTime = updated.NextFireTime;
            }

            // Same wording/format as LoadAlarmsAsync so the footer count cannot go stale after a switch.
            StatusMessage = BuildStatusMessage();
            return result.IsEnabled == requested;
        }
        catch (Exception ex)
        {
            StatusMessage = $"切替エラー: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            await RunDeferredProjectionCatchUpAsync();
        }
    }

    /// <summary>
    /// T109: builds a fresh row model holding every field of the just-saved alarm, so the collection
    /// can be replaced (and that one row redrawn) without JSON round-trips. The saved instance is
    /// authoritative for IsEnabled / NextFireTime / OS registration state; nothing is dropped.
    /// </summary>
    private static LocalAlarmModel CopySavedState(LocalAlarmModel saved) => new()
    {
        Id = saved.Id,
        Name = saved.Name,
        IsEnabled = saved.IsEnabled,
        Time = saved.Time,
        RepeatKind = saved.RepeatKind,
        ActiveWeekdays = saved.ActiveWeekdays,
        OnceOnDate = saved.OnceOnDate,
        ExcludeHolidays = saved.ExcludeHolidays,
        // T135: a row replacement must not drop the schedule's excluded dates.
        ExcludedDates = new List<DateOnly>(saved.ExcludedDates ?? new List<DateOnly>()),
        SnoozeMinutes = saved.SnoozeMinutes,
        SnoozeUntil = saved.SnoozeUntil,
        DeviceId = saved.DeviceId,
        TimeZoneId = saved.TimeZoneId,
        SyncStatus = saved.SyncStatus,
        OsRegistrationStatus = saved.OsRegistrationStatus,
        OsRegistrationError = saved.OsRegistrationError,
        VolumePercent = saved.VolumePercent,
        EnableVibration = saved.EnableVibration,
        SoundUri = saved.SoundUri,
        NextFireTime = saved.NextFireTime,
        HasHolidayCoverageWarning = saved.HasHolidayCoverageWarning
    };

    public async Task<bool> DeleteAlarmAsync(LocalAlarmModel alarm)
    {
        if (alarm is null) return false;
        return await DeleteAlarmAsync(alarm.Id);
    }

    public async Task<bool> DeleteAlarmAsync(string id)
    {
        // T123 M1: same as the toggle — claim the busy state before the first await, keep the UI context.
        if (IsBusy) return false;
        IsBusy = true;

        try
        {
            if (await IsCloudManagedAsync(id)) return false;   // T113 §3: no local delete of cloud rows

            var success = await _coordinator.DeleteAlarmAsync(id);
            if (success)
            {
                var item = Alarms.FirstOrDefault(a => a.Id == id);
                if (item is not null)
                {
                    Alarms.Remove(item);
                }
            }
            return success;
        }
        catch (Exception ex)
        {
            StatusMessage = $"削除エラー: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RunDeferredProjectionCatchUpAsync();
        }
        return false;
    }

    /// <summary>
    /// T113 §3 double defence. A sync-prefixed id is always cloud-owned; a legacy non-prefixed id that the
    /// current binding's manifest maps is cloud-owned too. The manifest is consulted lazily (a caller can
    /// pass an id that is not in the list), so a group row can never be turned into a manual edit just by
    /// handing its id to one of these methods.
    /// </summary>
    private async Task<bool> IsCloudManagedAsync(string? localAlarmId)
    {
        if (string.IsNullOrEmpty(localAlarmId))
        {
            return false;
        }

        if (localAlarmId.StartsWith(SyncLocalAlarmIdentity.IdPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (_cloudManagedLocalIds.Contains(localAlarmId))
        {
            return true;
        }

        var source = _groupSource;
        if (source is null)
        {
            return false;   // nothing to consult: the prefix check above is the whole contract
        }

        try
        {
            var result = await source.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            return GroupAlarmProjector.IsCloudManagedId(localAlarmId, result.Binding, result.Manifest);
        }
        catch (Exception)
        {
            // Unreadable source: keep the prefix-based decision instead of blocking manual alarms.
            return false;
        }
    }
}
