using System;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Where a row in the "group alarms" section of the alarm list comes from.
/// Deliberately neutral (no MAUI / view-model dependency) so the projector and this row can be
/// exercised by the pure sync test project.
/// </summary>
public enum GroupAlarmOrigin
{
    /// <summary>Stored on this device and owned by the current cloud binding: it rings.</summary>
    CloudApplied,

    /// <summary>In the last desired snapshot for this binding but not (yet) stored here: it does not ring.</summary>
    CloudPending,

    /// <summary>Cloud-prefixed id that does not belong to the current binding (or no binding is registered).</summary>
    CloudOrphan
}

/// <summary>
/// Read-only projection of a cloud (group) alarm row. Immutable on purpose: the view model replaces the
/// item in the observable collection when it changes (same approach as T109), so no INPC is needed.
/// <c>LocalAlarmId</c> is for display and de-duplication only; it must never be used to drive a manual
/// edit/toggle/delete path.
/// </summary>
public sealed record GroupAlarmRow(
    string RowKey,
    GroupAlarmOrigin Origin,
    string? LocalAlarmId,
    Guid? ScheduleId,
    string? GroupId,
    string GroupName,
    string TimeText,
    string DaysText,
    string? NextFireText,
    bool IsEnabled,
    string EnabledText,
    string? TimeZoneNote,
    bool SkipHolidays,
    bool HasHolidayCoverageWarning,
    bool IsRegistrationFailed,
    string? PendingText,
    string? WarningText,
    bool CanOpenGroup)
{
    /// <summary>Name shown when neither the stored alarm nor the desired item carries a label.</summary>
    public const string UnnamedGroupName = "名前のないグループ";

    /// <summary>True while the row only exists on the server side (nothing rings on this device).</summary>
    public bool IsPending => Origin == GroupAlarmOrigin.CloudPending;
}
