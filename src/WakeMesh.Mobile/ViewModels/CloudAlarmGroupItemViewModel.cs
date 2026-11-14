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

    public string ServerStatusText => $"サーバー保存済み (v{Version})";

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

    public string TargetsSummary => $"{_targets.Count} 台の端末に配信";

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
                    if (string.Equals(status.RetirementState, "retired", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.ReflectionStatusText = "端末退役済み";
                    }
                    else if (string.Equals(status.RetirementState, "revoked", StringComparison.OrdinalIgnoreCase))
                    {
                        vm.ReflectionStatusText = "端末取消済み (未確認)";
                    }
                    else if (status.AppliedRevision >= status.DesiredRevision && status.DesiredRevision > 0)
                    {
                        vm.ReflectionStatusText = $"適用報告済み (ACK済: rev {status.AppliedRevision})";
                    }
                    else
                    {
                        vm.ReflectionStatusText = $"反映待ち (最新 rev {status.DesiredRevision} / 適用 rev {status.AppliedRevision})";
                    }
                }
                targetList.Add(vm);
            }
        }
        _targets = targetList;
    }
}
