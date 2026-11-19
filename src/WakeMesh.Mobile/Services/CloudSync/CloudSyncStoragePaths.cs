using System;
using System.IO;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Single source of truth for the on-disk names of the cloud-sync side files. The producer
/// (<c>CloudManagementService</c>) and the read-only reader (<c>GroupAlarmSource</c>) must resolve the
/// same path for the same binding, otherwise the list would read a file nobody writes.
/// </summary>
public static class CloudSyncStoragePaths
{
    public const string ManifestPrefix = "sync_manifest_";
    public const string AppliedRevisionPrefix = "applied_rev_";
    public const string FileSuffix = ".json";

    /// <summary>Absolute path of the schedule→local-alarm manifest for <paramref name="binding"/>.</summary>
    public static string Manifest(string baseDirectory, CloudSyncBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(binding);
        return Path.Combine(baseDirectory, $"{ManifestPrefix}{binding.ToStorageKey()}{FileSuffix}");
    }

    /// <summary>Absolute path of the durable applied-revision high-water mark for <paramref name="binding"/>.</summary>
    public static string AppliedRevision(string baseDirectory, CloudSyncBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(binding);
        return Path.Combine(baseDirectory, $"{AppliedRevisionPrefix}{binding.ToStorageKey()}{FileSuffix}");
    }

    /// <summary>
    /// The storage directory the app writes to: the explicit override when one was supplied (tests,
    /// diagnostics), otherwise the shared no-backup app data directory.
    /// </summary>
    public static string ResolveBaseDirectory(string? customStorageDirectory = null) =>
        string.IsNullOrWhiteSpace(customStorageDirectory)
            ? WakeMesh.Mobile.Services.AlarmStoragePathHelper.GetBaseStorageDirectory()
            : customStorageDirectory;
}
