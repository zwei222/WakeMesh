using System;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Mapping record between a cloud schedule and the local alarm model.
/// </summary>
public sealed record SyncScheduleEntry(
    Guid ScheduleId,
    string LocalAlarmId,
    string GroupId,
    long ModifiedRevision);
