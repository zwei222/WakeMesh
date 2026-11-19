using System;
using System.Collections.Generic;
using System.Linq;
using WakeMesh.Client.Models;

namespace WakeMesh.Mobile.ViewModels;

public sealed class CloudAlarmGroupItemViewModel : ViewModelBase
{
    private readonly AlarmGroupDto _dto;
    private readonly IReadOnlyList<CloudAlarmTargetItemViewModel> _targets;

    public AlarmGroupDto Dto => _dto;
    public string Id => _dto.Id;
    public string Label => string.IsNullOrWhiteSpace(_dto.Label) ? "名称未設定グループ" : _dto.Label;
    public bool Enabled => _dto.Enabled;
    public long Version => _dto.Version;
    public string Repeat => _dto.Repeat;
    public string TimeZone => _dto.TimeZone;
    public bool ExcludeHolidays => _dto.ExcludeHolidays;
    public IReadOnlyList<CloudAlarmTargetItemViewModel> Targets => _targets;

    public string ServerStatusText => $"サーバーに保存済み（バージョン{Version}）";

    /// <summary>
    /// T80 P0-9: true when at least one target device has not applied this group yet, so the view can
    /// show the warn tag "未反映の端末あり". Retired/revoked devices are not counted (they will never
    /// apply it), and an unverified projection is not reported as pending.
    /// </summary>
    public bool HasUnappliedTargets => _targets.Any(t =>
        string.Equals(t.ReflectionStateKey, "pending", StringComparison.Ordinal) ||
        string.Equals(t.ReflectionStateKey, "warn", StringComparison.Ordinal));

    public string DaysSummary
    {
        get
        {
            if (string.Equals(_dto.Repeat, "once", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(_dto.Date) ? "一回限り" : $"一回限り ({_dto.Date})";
            }

            if (_dto.Days == null || _dto.Days.Count == 0)
            {
                return "繰り返しなし";
            }

            var dayNames = new Dictionary<int, string>
            {
                [0] = "日", [1] = "月", [2] = "火", [3] = "水", [4] = "木", [5] = "金", [6] = "土"
            };
            var validDays = _dto.Days.Where(d => dayNames.ContainsKey(d)).Distinct().OrderBy(d => d).ToArray();
            if (validDays.Length == 7)
            {
                return "毎日";
            }

            var weekdays = new[] { 1, 2, 3, 4, 5 };
            if (validDays.SequenceEqual(weekdays))
            {
                return "平日 (月〜金)";
            }

            var selected = _dto.Days.Distinct().OrderBy(d => d)
                .Select(d => dayNames.TryGetValue(d, out var n) ? n : $"不明({d})");
            return string.Join("・", selected);
        }
    }

    public string HolidaySummary => ExcludeHolidays ? "祝日除外: 有効" : "祝日除外: 無効";

    public string TargetsSummary => $"アラームを鳴らす端末: {_targets.Count} 台";

    public CloudAlarmGroupItemViewModel(
        AlarmGroupDto dto,
        IReadOnlyDictionary<Guid, string>? deviceNames = null,
        IReadOnlyDictionary<Guid, DeviceListItem>? deviceStatuses = null)
    {
        _dto = dto ?? throw new ArgumentNullException(nameof(dto));

        var targetList = new List<CloudAlarmTargetItemViewModel>();
        if (dto.Targets != null)
        {
            foreach (var t in dto.Targets)
            {
                string? name = null;
                deviceNames?.TryGetValue(t.DeviceId, out name);

                var vm = CloudAlarmTargetItemViewModel.FromDto(t, name);
                if (deviceStatuses != null && deviceStatuses.TryGetValue(t.DeviceId, out var status))
                {
                    // T80 P0-9: one judgement for both the wording and the mark, shared with the device
                    // row, so revokedUnconfirmed / retiring are never reported as a pending reflection.
                    var device = new CloudDeviceItemViewModel(status);
                    vm.ReflectionStateKey = device.StateKey;
                    vm.ReflectionStatusText = device.SyncStatusText;
                }
                targetList.Add(vm);
            }
        }
        _targets = targetList;
    }
}
