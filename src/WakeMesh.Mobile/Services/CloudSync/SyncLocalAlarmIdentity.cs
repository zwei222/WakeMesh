using System;

namespace WakeMesh.Mobile.Services.CloudSync;

/// <summary>
/// Single source of truth for deterministic, binding-scoped local alarm identities used by cloud sync.
/// Cloud-owned alarms always use <c>sync-{bindingStorageKey}-{scheduleId:N}</c>, so any process or
/// applier instance bound to the same <see cref="CloudSyncBinding"/> can re-derive ownership and
/// collect leftovers even when the sync manifest was never committed. Manual (user created) alarms
/// never match the prefix and are therefore never touched.
/// </summary>
public static class SyncLocalAlarmIdentity
{
    /// <summary>Prefix shared by every cloud-synced local alarm id.</summary>
    public const string IdPrefix = "sync-";

    /// <summary>Deterministic per-binding prefix. Different bindings can never collide.</summary>
    public static string GetBindingPrefix(CloudSyncBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return $"{IdPrefix}{binding.ToStorageKey()}-";
    }

    /// <summary>Deterministic local alarm id for one server schedule inside one binding scope.</summary>
    public static string GetLocalAlarmId(CloudSyncBinding binding, Guid scheduleId)
    {
        if (scheduleId == Guid.Empty)
        {
            throw new ArgumentException("ScheduleId cannot be empty.", nameof(scheduleId));
        }

        return $"{GetBindingPrefix(binding)}{scheduleId:N}";
    }

    /// <summary>
    /// True only when <paramref name="localAlarmId"/> belongs to this binding's cloud scope.
    /// Non-prefixed ids (manual alarms) are rejected without touching anything.
    /// </summary>
    public static bool TryParseScheduleId(CloudSyncBinding binding, string? localAlarmId, out Guid scheduleId)
    {
        scheduleId = Guid.Empty;
        if (string.IsNullOrEmpty(localAlarmId))
        {
            return false;
        }

        var prefix = GetBindingPrefix(binding);
        if (!localAlarmId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = localAlarmId.AsSpan(prefix.Length);
        return Guid.TryParseExact(suffix, "N", out scheduleId);
    }
}
