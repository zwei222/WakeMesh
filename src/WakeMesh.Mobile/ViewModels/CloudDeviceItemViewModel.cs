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

    /// <summary>
    /// T80 P0-9: fixed display contract for the status mark (colour + shape are chosen by the view
    /// from these exact keys, never from a hard-coded colour).
    /// ok / pending / unknown / warn / critical / inactive.
    /// </summary>
    public string StateKey
    {
        get
        {
            if (IsRetired || IsRevoked)
            {
                // A requested retirement/revocation the device has not confirmed yet is not "done".
                return _raw.CancelConfirmed ? "inactive" : "warn";
            }

            if (_raw.DesiredRevision == 0 && _raw.AppliedRevision == 0)
            {
                return _raw.HasSuccessfulSync switch
                {
                    true => "ok",
                    false => "pending",
                    null => "unknown"
                };
            }

            return _raw.AppliedRevision >= _raw.DesiredRevision ? "ok" : "pending";
        }
    }

    public string SyncStatusText
    {
        get
        {
            if (IsRetired)
            {
                return _raw.CancelConfirmed
                    ? "登録解除済み"
                    : "登録解除待ち（端末での解除待ち）";
            }
            if (IsRevoked)
            {
                return _raw.CancelConfirmed
                    ? "接続を無効にしました"
                    : "接続を無効にしました（端末のアラーム削除は未確認）";
            }
            if (_raw.DesiredRevision == 0 && _raw.AppliedRevision == 0)
            {
                // A zero revision is a valid acknowledged empty configuration; only claim success
                // when the server actually projected sync evidence.
                return _raw.HasSuccessfulSync switch
                {
                    true => "同期済み（設定なし）",
                    false => "初期同期待ち",
                    null => "初期同期待ち (同期状況未確認)"
                };
            }
            if (_raw.AppliedRevision >= _raw.DesiredRevision)
            {
                return $"端末から適用の報告あり（バージョン{_raw.AppliedRevision}）";
            }
            return $"反映待ち（最新バージョン{_raw.DesiredRevision} / 適用済みバージョン{_raw.AppliedRevision}）";
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
