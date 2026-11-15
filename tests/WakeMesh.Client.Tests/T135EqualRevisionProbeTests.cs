using System.Text;
using System.Text.Json;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using Xunit.Abstractions;

namespace WakeMesh.Client.Tests;

/// <summary>
/// T135: the equal-revision contract. An applier WITHOUT the probe keeps the T13 re-ACK behaviour; an
/// applier implementing <see cref="IEqualRevisionReapplyProbe"/> is asked once, and only a true answer
/// leads to a second apply at the same revision. A probe that cannot answer fails closed.
/// </summary>
public sealed class T135EqualRevisionProbeTests
{
    private readonly ITestOutputHelper _output;

    public T135EqualRevisionProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>Serves the same desired revision every time and counts the ACK posts.</summary>
    private sealed class FixedDesiredHandler(Guid deviceId, long revision) : HttpMessageHandler
    {
        public int AckPosts;
        public readonly List<string> AckBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref AckPosts);
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                lock (AckBodies) AckBodies.Add(body);
                var ack = JsonSerializer.Serialize(new { deviceId, appliedRevision = revision, status = "accepted" });
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(ack, Encoding.UTF8, "application/json"),
                };
            }

            var desired = JsonSerializer.Serialize(new
            {
                deviceId,
                revision,
                receiptId = Guid.NewGuid(),
                items = Array.Empty<object>(),
                tombstones = Array.Empty<object>(),
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(desired, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ProbeApplier : IDesiredApplier, IEqualRevisionReapplyProbe
    {
        private bool _needsReapply;

        public ProbeApplier(bool needsReapply) => _needsReapply = needsReapply;

        public int Calls { get; private set; }
        public int ProbeCalls { get; private set; }
        public Func<Task<bool>>? ProbeOverride { get; set; }

        public Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto snapshot, CancellationToken cancellationToken = default)
        {
            Calls++;
            _needsReapply = false;   // a successful apply brings the local payload up to date
            return Task.FromResult(new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>()));
        }

        public Task<bool> RequiresReapplyAtEqualRevisionAsync(CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return ProbeOverride?.Invoke() ?? Task.FromResult(_needsReapply);
        }
    }

    /// <summary>Same shape as the T13 double: no probe capability at all.</summary>
    private sealed class PlainApplier : IDesiredApplier
    {
        public int Calls { get; private set; }

        public Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto snapshot, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>()));
        }
    }

    private static WakeMeshApiClient MockApi(HttpMessageHandler handler, Guid deviceId)
    {
        var credentials = new MemoryDeviceCredentialStore();
        credentials.Set(deviceId, "cred");
        return WakeMeshApiClient.CreateForTests(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new StaticAccessTokenProvider("token"),
            credentials);
    }

    [Fact]
    public async Task ProbeTrue_AtEqualRevision_ReappliesOnceAndAcksSuccess()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FixedDesiredHandler(deviceId, revision: 5);
        var store = new MemoryAppliedRevisionStore();
        var applier = new ProbeApplier(needsReapply: false);
        var orchestrator = new DesiredSyncOrchestrator(MockApi(handler, deviceId), applier, store);

        var first = await orchestrator.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, first.Status);
        Assert.Equal(1, applier.Calls);
        Assert.Equal(5L, (await store.GetAppliedRevisionAsync(deviceId)) ?? 0L);

        // The device is upgraded: its applied payload predates the current item format.
        applier.ProbeOverride = () => Task.FromResult(true);
        var second = await orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"probe-true status={second.Status} applies={applier.Calls} " +
                          $"committed={await store.GetAppliedRevisionAsync(deviceId)} acks={handler.AckPosts}");

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, second.Status);
        Assert.Equal(2, applier.Calls);                                   // one extra apply, at the same revision
        Assert.Equal(5L, (await store.GetAppliedRevisionAsync(deviceId)) ?? 0L);   // equal commit, not advanced
        Assert.Equal("success", AckResultOf(handler.AckBodies[^1]));
        Assert.Equal(2, handler.AckPosts);
    }

    [Fact]
    public async Task ProbeFalse_AtEqualRevision_OnlyReAcks()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FixedDesiredHandler(deviceId, revision: 5);
        var store = new MemoryAppliedRevisionStore();
        var applier = new ProbeApplier(needsReapply: false);
        var orchestrator = new DesiredSyncOrchestrator(MockApi(handler, deviceId), applier, store);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await orchestrator.SyncDeviceAsync(deviceId)).Status);
        var second = await orchestrator.SyncDeviceAsync(deviceId);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, second.Status);
        Assert.Equal(1, applier.Calls);
        Assert.Equal(1, applier.ProbeCalls);
        Assert.Equal(5L, (await store.GetAppliedRevisionAsync(deviceId)) ?? 0L);
        Assert.Equal("success", AckResultOf(handler.AckBodies[^1]));
    }

    [Fact]
    public async Task ProbeThrows_FailsClosedWithNoAckAndNoMutation()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FixedDesiredHandler(deviceId, revision: 5);
        var store = new MemoryAppliedRevisionStore();
        var applier = new ProbeApplier(needsReapply: false);
        var orchestrator = new DesiredSyncOrchestrator(MockApi(handler, deviceId), applier, store);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await orchestrator.SyncDeviceAsync(deviceId)).Status);
        var acksAfterFirst = handler.AckPosts;

        applier.ProbeOverride = () => throw new InvalidOperationException("manifest unreadable");
        var outcome = await orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"probe-throw status={outcome.Status} kind={outcome.ErrorKind} code={outcome.ErrorCode} " +
                          $"applies={applier.Calls} acks={handler.AckPosts} committed={await store.GetAppliedRevisionAsync(deviceId)}");

        Assert.Equal(DesiredSyncStatus.ApplyIncompleteNoAck, outcome.Status);
        Assert.Equal(ApiErrorKind.Unexpected, outcome.ErrorKind);
        Assert.Equal("equalRevisionProbeFailed", outcome.ErrorCode);
        Assert.DoesNotContain("manifest unreadable", outcome.ErrorCode ?? string.Empty);
        Assert.Equal(1, applier.Calls);              // no apply
        Assert.Equal(acksAfterFirst, handler.AckPosts);   // no ACK at all
        Assert.Equal(5L, (await store.GetAppliedRevisionAsync(deviceId)) ?? 0L);
    }

    [Fact]
    public async Task ProbeCanceled_FailsClosedAndACanceledCycleSendsNoAck()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FixedDesiredHandler(deviceId, revision: 5);
        var store = new MemoryAppliedRevisionStore();
        var applier = new ProbeApplier(needsReapply: false);
        var orchestrator = new DesiredSyncOrchestrator(MockApi(handler, deviceId), applier, store);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await orchestrator.SyncDeviceAsync(deviceId)).Status);
        var acksAfterFirst = handler.AckPosts;

        // The probe cancels its own work without the caller's token being canceled.
        applier.ProbeOverride = () => throw new OperationCanceledException("probe timed out");
        var selfCanceled = await orchestrator.SyncDeviceAsync(deviceId);
        _output.WriteLine($"probe-self-canceled status={selfCanceled.Status} kind={selfCanceled.ErrorKind} " +
                          $"code={selfCanceled.ErrorCode} acks={handler.AckPosts}");

        Assert.Equal(DesiredSyncStatus.ApplyIncompleteNoAck, selfCanceled.Status);
        Assert.Equal("equalRevisionProbeFailed", selfCanceled.ErrorCode);
        Assert.Equal(acksAfterFirst, handler.AckPosts);
        Assert.Equal(1, applier.Calls);

        // Caller cancellation: no ACK and no apply either.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = await orchestrator.SyncDeviceAsync(deviceId, cts.Token);
        Assert.Equal(DesiredSyncStatus.CanceledNoAck, canceled.Status);
        Assert.Equal(acksAfterFirst, handler.AckPosts);
        Assert.Equal(1, applier.Calls);
    }

    // ---- the unchanged T13 contract: no probe → plain re-ACK -----------------

    [Fact]
    public async Task WithoutTheProbe_EqualRevisionIsStillReAckedWithoutApplying()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FixedDesiredHandler(deviceId, revision: 3);
        var store = new MemoryAppliedRevisionStore();
        var applier = new PlainApplier();
        var orchestrator = new DesiredSyncOrchestrator(MockApi(handler, deviceId), applier, store);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, (await orchestrator.SyncDeviceAsync(deviceId)).Status);
        var second = await orchestrator.SyncDeviceAsync(deviceId);

        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, second.Status);
        Assert.Equal(1, applier.Calls);
        Assert.False(typeof(IEqualRevisionReapplyProbe).IsAssignableFrom(applier.GetType()));
        Assert.Equal("success", AckResultOf(handler.AckBodies[^1]));
    }

    private static string? AckResultOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("result", out var lower)) return lower.GetString();
        return doc.RootElement.TryGetProperty("Result", out var upper) ? upper.GetString() : null;
    }
}
