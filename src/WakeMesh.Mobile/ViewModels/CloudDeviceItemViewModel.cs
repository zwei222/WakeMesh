using System;
using WakeMesh.Client.Models;

namespace WakeMesh.Mobile.ViewModels;

public sealed class CloudDeviceItemViewModel : ViewModelBase
{
    private readonly DeviceListItem _raw;
    private readonly bool _isCurrentDevice;

    public DeviceListItem Raw => _raw;
    public Guid Id => _raw.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(_raw.DisplayName) ? "名称未設定端末" : _raw.DisplayName;
    public string Status => _raw.Status;
    public long DesiredRevision => _raw.DesiredRevision;
    public long AppliedRevision => _raw.AppliedRevision;
    public string RetirementState => _raw.RetirementState;
    public bool CancelConfirmed => _raw.CancelConfirmed;
    public bool IsCurrentDevice => _isCurrentDevice;

    public string CurrentDeviceBadge => _isCurrentDevice ? "（この端末）" : string.Empty;

    public bool IsRetired => string.Equals(_raw.RetirementState, "retired", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_raw.RetirementState, "retiring", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_raw.Status, "Retiring", StringComparison.OrdinalIgnoreCase);

    public bool IsRevoked => string.Equals(_raw.RetirementState, "revoked", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_raw.RetirementState, "revokedUnconfirmed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_raw.Status, "Revoked", StringComparison.OrdinalIgnoreCase);
    public bool IsActive => !IsRetired && !IsRevoked;

    public bool CanRetire => IsActive;
    public bool CanRevoke => !IsRevoked;

    public string SyncStatusText
    {
        get
        {
            if (IsRetired)
            {
                return _raw.CancelConfirmed ? "退役済み (取消確認済)" : "退役処理中 (取消未確認)";
            }
            if (IsRevoked)
            {
                return _raw.CancelConfirmed ? "即時取消完了" : "即時取消済み (取消未確認)";
            }
            if (_raw.DesiredRevision == 0 && _raw.AppliedRevision == 0)
            {
                return "初期同期待ち";
            }
            if (_raw.AppliedRevision >= _raw.DesiredRevision)
            {
                return $"適用報告済み (ACK済: rev {_raw.AppliedRevision})";
            }
            return $"反映待ち (最新 rev {_raw.DesiredRevision} / 適用 rev {_raw.AppliedRevision})";
        }
    }

    public string SyncStatusColorHex
    {
        get
        {
            if (IsRetired || IsRevoked)
            {
                return "#F43F5E"; // StatusRose
            }
            if (_raw.AppliedRevision >= _raw.DesiredRevision && _raw.DesiredRevision > 0)
            {
                return "#10B981"; // StatusGreen
            }
            return "#F59E0B"; // StatusAmber
        }
    }

    public CloudDeviceItemViewModel(DeviceListItem raw, bool isCurrentDevice = false)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _isCurrentDevice = isCurrentDevice;
    }
}
