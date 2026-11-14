using System;
using System.Collections.Generic;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Manifest recording all server-managed schedule-to-local-alarm mappings for a specific CloudSyncBinding.
/// Separate from LocalAlarmModel so local manual alarms are preserved and never mistaken for cloud schedules.
/// </summary>
public sealed class CloudSyncManifest
{
    public int SchemaVersion { get; set; } = 1;
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
            Schedules = new Dictionary<Guid, SyncScheduleEntry>(Schedules)
        };
    }
}
