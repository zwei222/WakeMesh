using System.Collections.Concurrent;

namespace WakeMesh.Client.Tests;

/// <summary>Test double for durable applied-revision high-water marks.</summary>
public sealed class MemoryAppliedRevisionStore : IAppliedRevisionStore
{
    private readonly ConcurrentDictionary<Guid, long> _map = new();

    public Task<long?> GetAppliedRevisionAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(_map.TryGetValue(deviceId, out var v) ? (long?)v : null);

    public Task CommitAppliedRevisionAsync(Guid deviceId, long revision, CancellationToken cancellationToken = default)
    {
        _map.AddOrUpdate(deviceId, revision, (_, existing) => revision > existing ? revision : existing);
        return Task.CompletedTask;
    }
}
