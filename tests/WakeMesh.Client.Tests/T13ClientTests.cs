using System.Net;
using System.Text;
using System.Text.Json;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using Xunit.Abstractions;

namespace WakeMesh.Client.Tests;

/// <summary>T13 Client bounded fixes: equal-rev re-ACK, required store, desired validation, typed faults.</summary>
public sealed class T13ClientTests : IClassFixture<ClientApiFactory>
{
    private readonly ClientApiFactory _f;
    private readonly ITestOutputHelper _o;

    public T13ClientTests(ClientApiFactory f, ITestOutputHelper o)
    {
        _f = f;
        _o = o;
    }

    private sealed class Applier(Func<DesiredSnapshotDto, DesiredApplyResult> fn) : IDesiredApplier
    {
        public readonly List<long> Order = new();
        public int Calls;

        public Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto s, CancellationToken c = default)
        {
            Interlocked.Increment(ref Calls);
            lock (Order) Order.Add(s.Revision);
            return Task.FromResult(fn(s));
        }
    }

    private static DesiredApplyResult Ok() => new(true, true, Array.Empty<AckFailureDto>());

    /// <summary>Drops the first N POST /acks (simulates lost ACK / 503).</summary>
    private sealed class DropAckHandler(int drops) : DelegatingHandler
    {
        private int _left = drops;
        public int AckPosts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/acks", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref AckPosts);
                if (Interlocked.Decrement(ref _left) >= 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
            }

            return base.SendAsync(r, c);
        }
    }

    private sealed class ScriptedDesired(Guid id, params long[] revs) : HttpMessageHandler
    {
        private int _i;
        public int AckCount;
        public readonly List<string> AckBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            if (r.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref AckCount);
                var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(c).ConfigureAwait(false);
                lock (AckBodies) AckBodies.Add(body);
                var ack = JsonSerializer.Serialize(new { deviceId = id, appliedRevision = 0, status = "accepted" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ack, Encoding.UTF8, "application/json"),
                };
            }

            var rev = revs[Math.Min(_i++, revs.Length - 1)];
            var desired = JsonSerializer.Serialize(new
            {
                deviceId = id,
                revision = rev,
                receiptId = Guid.NewGuid(),
                items = Array.Empty<object>(),
                tombstones = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(desired, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WakeMeshApiClient MockApi(HttpMessageHandler h, Guid id)
    {
        var creds = new MemoryDeviceCredentialStore();
        creds.Set(id, "cred");
        return WakeMeshApiClient.CreateForTests(
            new HttpClient(h) { BaseAddress = new Uri("http://localhost/") },
            new StaticAccessTokenProvider("t"),
            creds);
    }

    // ---- 1. Real API: commit then ACK 503 → re-sync same revision: no re-apply, new receipt success ACK, retire completes ----

    [Fact]
    public async Task Ack503AfterCommit_ResyncSameRevision_ReAcksWithoutReapply_RetireCompletes()
    {
        var drop = new DropAckHandler(1);
        var creds = new MemoryDeviceCredentialStore();
        var tok = new StaticAccessTokenProvider(_f.Auth.CreateAccessToken());
        var dev = WakeMeshApiClient.CreateForTests(_f.CreateDefaultClient(drop), tok, creds);
        var owner = WakeMeshApiClient.CreateForTests(_f.CreateClient(), tok, creds);
        var reg = await owner.RegisterDeviceAsync("t13-ack503");
        Assert.True(reg.IsSuccess);
        var id = reg.Value!.Id;
        await owner.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "t13-ack503-g", "P", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, id, "06:00", "default", 50, true, true) }));
        var retire = await owner.RetireDeviceAsync(id);
        Assert.True(retire.IsSuccess);

        var store = new MemoryAppliedRevisionStore();
        var applier = new Applier(_ => Ok());
        var orch = new DesiredSyncOrchestrator(dev, applier, store);

        var s1 = await orch.SyncDeviceAsync(id);
        var committed = await store.GetAppliedRevisionAsync(id);
        Assert.NotNull(committed);
        Assert.True(committed!.Value >= 1, $"expected commit before failed ACK; s1={s1.Status}");
        Assert.Equal(1, applier.Calls);
        Assert.Equal(DesiredSyncStatus.TransportFailed, s1.Status);

        var s2 = await orch.SyncDeviceAsync(id);
        var d = await owner.GetDeviceAsync(id);
        _o.WriteLine(
            $"s1={s1.Status}/{s1.ErrorKind} s2={s2.Status}/{s2.ErrorCode} applies={applier.Calls} " +
            $"store={committed} server desired={d.Value!.DesiredRevision} applied={d.Value.AppliedRevision} " +
            $"status={d.Value.Status} cancelConfirmed={d.Value.CancelConfirmed} ackPosts={drop.AckPosts}");

        Assert.Equal(1, applier.Calls); // no second apply
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, s2.Status);
        Assert.Equal(d.Value.DesiredRevision, d.Value.AppliedRevision);
        Assert.True(d.Value.CancelConfirmed);
        Assert.True(drop.AckPosts >= 2);
    }

    // ---- 2. Required non-null store; no implicit memory fallback; cross-instance shared store ----

    [Fact]
    public void AppliedRevisionStore_Null_Throws()
    {
        var id = Guid.NewGuid();
        var api = MockApi(new ScriptedDesired(id, 1), id);
        Assert.Throws<ArgumentNullException>(() => new DesiredSyncOrchestrator(api, new Applier(_ => Ok()), null!));
    }

    [Fact]
    public async Task SharedStore_TwoOrchestrators_EqualRev_NoReapply_LowerIsStale()
    {
        var id = Guid.NewGuid();
        var h = new ScriptedDesired(id, 2, 2, 1);
        var api = MockApi(h, id);
        var app = new Applier(_ => Ok());
        var store = new MemoryAppliedRevisionStore();

        var ra = await new DesiredSyncOrchestrator(api, app, store).SyncDeviceAsync(id);
        var rb = await new DesiredSyncOrchestrator(api, app, store).SyncDeviceAsync(id);
        // Force a lower revision fetch after equal-rev re-ACK path consumed revs[1]=2
        var rc = await new DesiredSyncOrchestrator(api, app, store).SyncDeviceAsync(id);

        _o.WriteLine($"a={ra.Status} b={rb.Status} c={rc.Status} order=[{string.Join(",", app.Order)}] acks={h.AckCount}");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, ra.Status);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, rb.Status); // equal rev: re-ACK, no apply
        Assert.Equal(DesiredSyncStatus.StaleRevisionSkipped, rc.Status);
        Assert.Equal(new long[] { 2 }, app.Order.ToArray());
        Assert.Equal(2, h.AckCount); // first apply ACK + equal re-ACK; stale sends none
    }

    [Fact]
    public async Task NeverCommitted_RevisionZero_IsApplied_NotTreatedAsStale()
    {
        var id = Guid.NewGuid();
        var h = new ScriptedDesired(id, 0);
        var api = MockApi(h, id);
        var app = new Applier(_ => Ok());
        var store = new MemoryAppliedRevisionStore();
        Assert.Null(await store.GetAppliedRevisionAsync(id));

        var r = await new DesiredSyncOrchestrator(api, app, store).SyncDeviceAsync(id);
        _o.WriteLine($"status={r.Status} applies={app.Calls} store={await store.GetAppliedRevisionAsync(id)}");
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, r.Status);
        Assert.Equal(1, app.Calls);
        Assert.Equal(0L, await store.GetAppliedRevisionAsync(id));
    }

    // ---- 3. Desired schema validation → InvalidResponse, no apply/commit/ACK ----

    private sealed class FixedBody(string desired) : HttpMessageHandler
    {
        public int Acks;
        public string? LastAck;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            if (r.Method == HttpMethod.Post)
            {
                Acks++;
                LastAck = await r.Content!.ReadAsStringAsync(c).ConfigureAwait(false);
            }

            var body = r.Method == HttpMethod.Get
                ? desired
                : "{\"deviceId\":\"" + Guid.Empty + "\",\"appliedRevision\":0,\"status\":\"accepted\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    public static IEnumerable<object[]> InvalidDesiredBodies()
    {
        yield return new object[] { "negRevision", "{\"deviceId\":\"{ID}\",\"revision\":-1,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}" };
        yield return new object[] { "missingItems", "{\"deviceId\":\"{ID}\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"tombstones\":[]}" };
        yield return new object[] { "nullItemsTombs", "{\"deviceId\":\"{ID}\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":null,\"tombstones\":null}" };
        yield return new object[] { "emptyReceipt", "{\"deviceId\":\"{ID}\",\"revision\":5,\"receiptId\":\"00000000-0000-0000-0000-000000000000\",\"items\":[],\"tombstones\":[]}" };
        yield return new object[] { "emptyDeviceId", "{\"deviceId\":\"00000000-0000-0000-0000-000000000000\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}" };
        yield return new object[] { "nullItemElement", "{\"deviceId\":\"{ID}\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[null],\"tombstones\":[]}" };
        yield return new object[] { "nullTombElement", "{\"deviceId\":\"{ID}\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[null]}" };
        yield return new object[] { "deviceMismatch", "{\"deviceId\":\"" + Guid.NewGuid() + "\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}" };
    }

    [Theory]
    [MemberData(nameof(InvalidDesiredBodies))]
    public async Task InvalidDesired_IsInvalidResponse_NoApplyNoCommitNoAck(string name, string tmpl)
    {
        var id = Guid.NewGuid();
        var h = new FixedBody(tmpl.Replace("{ID}", id.ToString(), StringComparison.Ordinal));
        var store = new MemoryAppliedRevisionStore();
        var app = new Applier(_ => Ok());
        DesiredSyncOutcome? outcome = null;
        Exception? threw = null;
        try
        {
            outcome = await new DesiredSyncOrchestrator(MockApi(h, id), app, store).SyncDeviceAsync(id);
        }
        catch (Exception ex)
        {
            threw = ex;
        }

        _o.WriteLine($"{name}: outcome={outcome?.Status}/{outcome?.ErrorKind}/{outcome?.ErrorCode} threw={threw?.GetType().Name} applies={app.Calls} store={await store.GetAppliedRevisionAsync(id)} acks={h.Acks}");
        Assert.Null(threw);
        Assert.NotNull(outcome);
        Assert.Equal(DesiredSyncStatus.TransportFailed, outcome!.Status);
        Assert.Equal(ApiErrorKind.InvalidResponse, outcome.ErrorKind);
        Assert.Equal(0, app.Calls);
        Assert.Null(await store.GetAppliedRevisionAsync(id));
        Assert.Equal(0, h.Acks);
    }

    [Fact]
    public async Task GetDesired_NegativeRevision_IsInvalidResponse()
    {
        var id = Guid.NewGuid();
        var h = new FixedBody("{\"deviceId\":\"" + id + "\",\"revision\":-3,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}");
        var api = MockApi(h, id);
        var res = await api.GetDesiredAsync(id);
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.InvalidResponse, res.Kind);
        Assert.Null(res.Value);
    }

    // ---- 4. Gate cancel + applier general exception → fixed typed results (no message leak) ----

    [Fact]
    public async Task ApplierThrows_IsTyped_NoMessageLeak_NoAck()
    {
        var id = Guid.NewGuid();
        var secret = "ALARM-SECRET-PATH-/data/x";
        var h = new FixedBody("{\"deviceId\":\"" + id + "\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}");
        var orch = new DesiredSyncOrchestrator(
            MockApi(h, id),
            new Applier(_ => throw new InvalidOperationException(secret)),
            new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(id);
        _o.WriteLine($"applierThrows => {outcome.Status}/{outcome.ErrorKind}/{outcome.ErrorCode}/{outcome}");
        Assert.Equal(DesiredSyncStatus.ApplyIncompleteNoAck, outcome.Status);
        Assert.Equal(ApiErrorKind.Unexpected, outcome.ErrorKind);
        Assert.Equal("applierFailed", outcome.ErrorCode);
        Assert.DoesNotContain(secret, outcome.ErrorCode ?? "");
        Assert.Equal(0, h.Acks);
    }

    [Fact]
    public async Task CancelWhileWaitingGate_IsTypedCanceled()
    {
        var id = Guid.NewGuid();
        var h = new FixedBody("{\"deviceId\":\"" + id + "\",\"revision\":5,\"receiptId\":\"" + Guid.NewGuid() + "\",\"items\":[],\"tombstones\":[]}");
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new SlowApplier(gateHeld, release.Task);
        var api = MockApi(h, id);
        var store = new MemoryAppliedRevisionStore();
        var first = new DesiredSyncOrchestrator(api, slow, store).SyncDeviceAsync(id);
        await gateHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(50);
        var second = await new DesiredSyncOrchestrator(api, slow, store).SyncDeviceAsync(id, cts.Token);
        release.TrySetResult();
        await first;
        _o.WriteLine($"cancelOnGate => {second.Status}/{second.ErrorKind}/{second.ErrorCode}");
        Assert.Equal(DesiredSyncStatus.CanceledNoAck, second.Status);
        Assert.Equal(ApiErrorKind.Canceled, second.ErrorKind);
    }

    private sealed class SlowApplier(TaskCompletionSource held, Task release) : IDesiredApplier
    {
        public async Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto s, CancellationToken c = default)
        {
            held.TrySetResult();
            await release.WaitAsync(c).ConfigureAwait(false);
            return Ok();
        }
    }

    // ---- 5. Register cancel: pre-send vs in-flight headers-not-yet ----

    private sealed class CommitThenCancel(HttpMessageHandler inner, CancellationTokenSource cts) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            var res = await base.SendAsync(r, CancellationToken.None).ConfigureAwait(false);
            res.Dispose();
            cts.Cancel();
            c.ThrowIfCancellationRequested();
            return res; // unreachable when canceled
        }
    }

    [Fact]
    public async Task Register_InFlightCancelBeforeHeaders_UnknownOutcome_RecoveryNeeded_NullServerId()
    {
        using var cts = new CancellationTokenSource();
        var tok = new StaticAccessTokenProvider(_f.Auth.CreateAccessToken());
        var api = WakeMeshApiClient.CreateForTests(
            _f.CreateDefaultClient(new CommitThenCancel(new HttpClientHandler(), cts)),
            tok,
            new MemoryDeviceCredentialStore());
        var owner = WakeMeshApiClient.CreateForTests(_f.CreateClient(), tok);
        var res = await api.RegisterDeviceAsync("t13-p5", cancellationToken: cts.Token);
        var list = await owner.ListDevicesAsync();
        var orphan = list.Value!.Where(d => d.DisplayName == "t13-p5").ToList();
        _o.WriteLine($"kind={res.Kind} recovery={res.RecoveryNeeded} sid={res.ServerDeviceId} serverHas={orphan.Count}");
        Assert.Equal(ApiErrorKind.UnknownOutcome, res.Kind);
        Assert.True(res.RecoveryNeeded);
        Assert.Null(res.ServerDeviceId);
        Assert.Equal("unknownOutcome", res.ErrorCode);
    }

    [Fact]
    public async Task Register_CancelBeforeSend_IsCanceled_NoRecovery()
    {
        var handler = new DelayingHandler(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"), new MemoryDeviceCredentialStore());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = await api.RegisterDeviceAsync("no-send", cancellationToken: cts.Token);
        Assert.Equal(ApiErrorKind.Canceled, res.Kind);
        Assert.False(res.RecoveryNeeded);
        Assert.Null(res.ServerDeviceId);
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
