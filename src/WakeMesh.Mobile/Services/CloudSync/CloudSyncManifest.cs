using System;
using System.Collections.Generic;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Manifest recording all server-managed schedule-to-local-alarm mappings for a specific CloudSyncBinding.
/// Separate from LocalAlarmModel so local manual alarms are preserved and never mistaken for cloud schedules.
/// </summary>
public sealed class CloudSyncManifest
{
    /// <summary>
    /// T135: format version of the per-item payload already applied to this device's alarms.
    /// 0 (the default for manifests written by older builds, which had no such key) means the local
    /// alarms may predate <c>ExcludedDates</c> and must be re-applied once at an equal revision.
    /// Reusing <see cref="SchemaVersion"/> is not possible: both old and new manifests carry 1 there.
    /// </summary>
    public const int CurrentAppliedItemFormat = 1;

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Payload format of the applied items. 0 for manifests written before T135.</summary>
    public int AppliedItemFormat { get; set; }

    public string ApiBaseUrl { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public Guid ServerDeviceId { get; set; }
    public long RegistrationGeneration { get; set; }
    public Dictionary<Guid, SyncScheduleEntry> Schedules { get; set; } = new();

    public CloudSyncManifest Clone()
    {
        return new CloudSyncManifest
        {
            SchemaVersion = SchemaVersion,
            ApiBaseUrl = ApiBaseUrl,
            AccountId = AccountId,
            ServerDeviceId = ServerDeviceId,
            RegistrationGeneration = RegistrationGeneration,
            AppliedItemFormat = AppliedItemFormat,
            Schedules = new Dictionary<Guid, SyncScheduleEntry>(Schedules)
        };
    }
}
