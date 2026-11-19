using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WakeMesh.Client.Http;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;

namespace WakeMesh.Client.Tests;

/// <summary>T11 security fixes: revision monotonicity, redirect/ctor lockdown, register durability, typed faults.</summary>
public sealed class T11SecurityTests : IClassFixture<ClientApiFactory>
{
    private readonly ClientApiFactory _factory;

    public T11SecurityTests(ClientApiFactory factory) => _factory = factory;

    private (WakeMeshApiClient Client, StaticAccessTokenProvider Tokens, MemoryDeviceCredentialStore Creds) CreateSut()
    {
        var http = _factory.CreateClient();
        var tokens = new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken());
        var creds = new MemoryDeviceCredentialStore();
        var client = WakeMeshApiClient.CreateForTests(http, tokens, creds);
        return (client, tokens, creds);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // --- 1. Concurrent / multi-orchestrator revision monotonicity ---

    private sealed class GatedApplier : IDesiredApplier
    {
        public long OsRevision = -1;
        public readonly List<long> Order = new();
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _n;

        public async Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto s, CancellationToken c = default)
        {
            if (Interlocked.Increment(ref _n) == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(c).ConfigureAwait(false);
            }

            lock (Order)
            {
                Order.Add(s.Revision);
                OsRevision = s.Revision;
            }

            return new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>());
        }
    }

    [Fact]
    public async Task ConcurrentSync_SameDevice_StaleRevision_DoesNotOverwrite_OrAckBehind()
    {
        var (api, _, _) = CreateSut();
        var reg = await api.RegisterDeviceAsync("t11-concurrent");
        Assert.True(reg.IsSuccess);
        var id = reg.Value!.Id;

        AlarmGroupWriteRequest Req(string t) => new(
            "t11-cc-g", "C", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, id, t, "default", 50, true, true) });

        var c1 = await api.CreateAlarmGroupAsync(Req("06:00"));
        Assert.True(c1.IsSuccess);

        var revisions = new MemoryAppliedRevisionStore();
        var applier = new GatedApplier();
        var orch = new DesiredSyncOrchestrator(api, applier, revisions);

        var syncA = orch.SyncDeviceAsync(id);
        await applier.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var c2 = await api.ReplaceAlarmGroupAsync("t11-cc-g", Req("07:00"), c1.Value!.Group.Version);
        Assert.True(c2.IsSuccess, c2.Kind.ToString());

        // B must block on the shared device gate while A still holds it inside ApplyAsync.
        var syncB = orch.SyncDeviceAsync(id);
        await Task.Delay(100);
        applier.ReleaseFirst.TrySetResult();
        var outA = await syncA.WaitAsync(TimeSpan.FromSeconds(15));
        var outB = await syncB.WaitAsync(TimeSpan.FromSeconds(15));

        var detail = await api.GetDeviceAsync(id);
        Assert.True(detail.IsSuccess);

        // OS and server must not diverge with a stale applied revision behind a newer one.
        Assert.True(applier.OsRevision >= detail.Value!.AppliedRevision,
            $"os={applier.OsRevision} serverApplied={detail.Value.AppliedRevision} order=[{string.Join(",", applier.Order)}] A={outA.Status} B={outB.Status}");
        Assert.Equal(detail.Value.DesiredRevision, detail.Value.AppliedRevision);
        Assert.DoesNotContain(applier.Order, r => r < applier.OsRevision && applier.Order.Last() == r && r != applier.OsRevision);
        // Final OS must be the max applied revision (no rev2 then rev1 collapse).
        Assert.Equal(applier.Order.Max(), applier.OsRevision);
        Assert.True(detail.Value.AppliedRevision >= 2);
    }

    [Fact]
    public async Task TwoOrchestratorInstances_ShareDeviceGate_AndRevisionStore()
    {
        var (api, _, _) = CreateSut();
        var reg = await api.RegisterDeviceAsync("t11-two-orch");
        var id = reg.Value!.Id;
        await api.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "t11-two-g", "T", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, id, "06:00", "default", 50, true, true) }));

        var store = new MemoryAppliedRevisionStore();
        var order = new List<long>();
        var applier = new RecordingApplier(s =>
        {
            lock (order) order.Add(s.Revision);
            return new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>());
        });

        var a = new DesiredSyncOrchestrator(api, applier, store);
        var b = new DesiredSyncOrchestrator(api, applier, store);

        var first = await a.SyncDeviceAsync(id);
        Assert.Equal(DesiredSyncStatus.AppliedAcknowledged, first.Status);
        var appliedAfterFirst = await store.GetAppliedRevisionAsync(id);
        Assert.True(appliedAfterFirst is >= 1);
        var appliedN = appliedAfterFirst!.Value;

        // Simulate newer desired then attempt stale: store already at N must reject lower.
        await store.CommitAppliedRevisionAsync(id, appliedN + 5);
        // Force path via public sync after bumping store ahead of server — next real sync should not ACK behind store.
        await api.ReplaceAlarmGroupAsync(
            "t11-two-g",
            new AlarmGroupWriteRequest(
                "t11-two-g", "T2", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(),
                new[] { new AlarmTargetDto(null, id, "07:00", "default", 50, true, true) }),
            ifMatchVersion: 1);

        var second = await b.SyncDeviceAsync(id);
        // Server desired > previous ACK but still must not apply/ACK anything < durable store.
        // After replace, server revision advances; if still < store high-water, skip.
        var storeNow = await store.GetAppliedRevisionAsync(id);
        Assert.True(storeNow is long sn && sn >= appliedN + 5);
        Assert.NotEqual(DesiredSyncStatus.AppliedAcknowledged, second.Status);
        Assert.Equal(DesiredSyncStatus.StaleRevisionSkipped, second.Status);
    }

    [Fact]
    public async Task AppliedRevisionStore_SurvivesOrchestratorRecreate_RejectsLowerRevision()
    {
        var store = new MemoryAppliedRevisionStore();
        var deviceId = Guid.NewGuid();
        await store.CommitAppliedRevisionAsync(deviceId, 2);

        var handler = new FixedJsonHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                deviceId,
                revision = 1,
                receiptId = Guid.NewGuid(),
                items = Array.Empty<object>(),
                tombstones = Array.Empty<object>(),
            }));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var creds = new MemoryDeviceCredentialStore();
        creds.Set(deviceId, "cred");
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"), creds);
        var applier = new RecordingApplier(_ => new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>()));
        var orch = new DesiredSyncOrchestrator(api, applier, store);
        var outcome = await orch.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.StaleRevisionSkipped, outcome.Status);
        Assert.Equal(0, applier.Calls);
        Assert.Equal(2L, await store.GetAppliedRevisionAsync(deviceId));
    }

    [Fact]
    public async Task SnapshotDeviceIdMismatch_IsInvalidResponse_NoApplyNoAck()
    {
        var deviceId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            deviceId = other,
            revision = 3,
            receiptId = Guid.NewGuid(),
            items = Array.Empty<object>(),
            tombstones = Array.Empty<object>(),
        });
        var handler = new FixedJsonHandler(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var creds = new MemoryDeviceCredentialStore();
        creds.Set(deviceId, "cred");
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"), creds);
        var applier = new RecordingApplier(_ => new DesiredApplyResult(true, true, Array.Empty<AckFailureDto>()));
        var orch = new DesiredSyncOrchestrator(api, applier, new MemoryAppliedRevisionStore());
        var outcome = await orch.SyncDeviceAsync(deviceId);
        Assert.Equal(DesiredSyncStatus.TransportFailed, outcome.Status);
        Assert.Equal(ApiErrorKind.InvalidResponse, outcome.ErrorKind);
        Assert.Equal(0, applier.Calls);
    }

    [Fact]
    public async Task SucceededWithFailures_DoesNotAckSuccess_OrCommitApplied()
    {
        var (api, _, _) = CreateSut();
        var reg = await api.RegisterDeviceAsync("t11-succ-fail");
        var id = reg.Value!.Id;
        await api.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
            "t11-sf-g", "S", true, "weekly", new[] { 1 }, null, "Asia/Tokyo", false, Array.Empty<string>(),
            new[] { new AlarmTargetDto(null, id, "06:00", "default", 50, true, true) }));

        var store = new MemoryAppliedRevisionStore();
        var applier = new RecordingApplier(_ => new DesiredApplyResult(
            Succeeded: true,
            Completed: true,
            Failures: new[] { new AckFailureDto(null, "partialItem", "x") }));
        var orch = new DesiredSyncOrchestrator(api, applier, store);
        var outcome = await orch.SyncDeviceAsync(id);
        Assert.Equal(DesiredSyncStatus.ApplyFailedAcknowledged, outcome.Status);
        Assert.Null(await store.GetAppliedRevisionAsync(id));
        var detail = await api.GetDeviceAsync(id);
        Assert.Equal(0, detail.Value!.AppliedRevision);
    }

    // --- 2. Public HttpClient / redirect lockdown ---

    [Fact]
    public void PublicApi_DoesNotExpose_HttpClientOrHandler_Constructors()
    {
        var ctors = typeof(WakeMeshApiClient).GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            Assert.DoesNotContain(ps, p => p.ParameterType == typeof(HttpClient));
            Assert.DoesNotContain(ps, p => typeof(HttpMessageHandler).IsAssignableFrom(p.ParameterType));
        }

        var factoryMethods = typeof(WakeMeshHttpClientFactory).GetMethods(BindingFlags.Public | BindingFlags.Static);
        foreach (var m in factoryMethods.Where(x => x.Name == "Create"))
        {
            Assert.DoesNotContain(m.GetParameters(), p => typeof(HttpMessageHandler).IsAssignableFrom(p.ParameterType));
            Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(HttpClient));
        }
    }

    [Fact]
    public void ProductionFactory_InnerMostHandler_DisallowsAutoRedirect()
    {
        var sockets = WakeMeshHttpClientFactory.CreateProductionSocketsHandlerForTests();
        Assert.False(sockets.AllowAutoRedirect);
        using var client = WakeMeshHttpClientFactory.Create("https://api.example.test/");
        Assert.Equal("https", client.BaseAddress!.Scheme);
    }

    [Fact]
    public async Task ProductionStylePipeline_Redirect_DoesNotLeak_DeviceCredential_OrBearer()
    {
        int pa = FreePort(), pb = FreePort();
        using var a = new HttpListener();
        a.Prefixes.Add($"http://127.0.0.1:{pa}/");
        a.Start();
        using var b = new HttpListener();
        b.Prefixes.Add($"http://127.0.0.1:{pb}/");
        b.Start();

        string? gotCred = null, gotAuth = null;
        var tb = Task.Run(async () =>
        {
            var c = await b.GetContextAsync();
            gotCred = c.Request.Headers["X-Device-Credential"];
            gotAuth = c.Request.Headers["Authorization"];
            c.Response.StatusCode = 401;
            c.Response.Close();
        });
        var ta = Task.Run(async () =>
        {
            var c = await a.GetContextAsync();
            c.Response.StatusCode = 302;
            c.Response.RedirectLocation = $"http://127.0.0.1:{pb}/steal";
            c.Response.Close();
        });

        // Even with an injected custom outer transport, production chain must refuse redirect follow.
        var inner = new HttpClientHandler { AllowAutoRedirect = true };
        using var http = WakeMeshHttpClientFactory.CreateForTests(
            new Uri($"http://127.0.0.1:{pa}/"),
            inner,
            pinHost: "127.0.0.1");
        var creds = new MemoryDeviceCredentialStore();
        var id = Guid.NewGuid();
        creds.Set(id, "DEVICE-SECRET-123");
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("OWNER-TOKEN"), creds);
        var res = await api.GetDesiredAsync(id);
        await Task.WhenAny(tb, Task.Delay(1500));
        Assert.Equal(ApiErrorKind.RedirectRefused, res.Kind);
        Assert.Null(gotCred);
        Assert.Null(gotAuth);
    }

    [Fact]
    public void SafePublicFactory_Create_FromHttpsUrl_CompilesShape()
    {
        using var api = WakeMeshApiClient.Create(
            "https://api.example.test/",
            new StaticAccessTokenProvider("t"),
            new MemoryDeviceCredentialStore());
        Assert.NotNull(api);
    }

    // --- 3. Register cancel-after-headers must still persist credential ---

    private sealed class CancelAfterHeadersHandler : DelegatingHandler
    {
        private readonly CancellationTokenSource _cts;
        public CancelAfterHeadersHandler(HttpMessageHandler inner, CancellationTokenSource cts) : base(inner) => _cts = cts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            _cts.Cancel();
            return response;
        }
    }

    private sealed class CtStore : IDeviceCredentialStore
    {
        public Dictionary<Guid, string> Map { get; } = new();
        public Exception? SaveException { get; set; }
        public bool ThrowOnCancel { get; set; } = true;

        public Task<string?> GetCredentialAsync(Guid d, CancellationToken c = default)
            => Task.FromResult(Map.TryGetValue(d, out var v) ? v : null);

        public Task SaveCredentialAsync(Guid d, string cred, CancellationToken c = default)
        {
            if (SaveException is not null) throw SaveException;
            if (ThrowOnCancel) c.ThrowIfCancellationRequested();
            Map[d] = cred;
            return Task.CompletedTask;
        }

        public Task DeleteCredentialAsync(Guid d, CancellationToken c = default)
        {
            Map.Remove(d);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Register_CancelAfterServerSuccess_StillPersistsCredential()
    {
        using var cts = new CancellationTokenSource();
        var http = _factory.CreateDefaultClient(new CancelAfterHeadersHandler(new HttpClientHandler(), cts));
        var store = new CtStore { ThrowOnCancel = true };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()), store);
        var owner = WakeMeshApiClient.CreateForTests(
            _factory.CreateClient(),
            new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()));

        var res = await api.RegisterDeviceAsync("t11-orphan", cancellationToken: cts.Token);
        Assert.True(res.IsSuccess, $"kind={res.Kind} code={res.ErrorCode} recovery={res.RecoveryNeeded} id={res.ServerDeviceId}");
        Assert.NotNull(res.Value);
        Assert.False(string.IsNullOrWhiteSpace(res.Value!.Credential));
        Assert.Single(store.Map);
        Assert.Equal(res.Value.Id, store.Map.Keys.Single());
        Assert.False(res.RecoveryNeeded);

        var after = await owner.ListDevicesAsync();
        Assert.Contains(after.Value!, d => d.DisplayName == "t11-orphan");
    }

    [Fact]
    public async Task Register_PersistFailure_ReturnsTypedError_WithServerDeviceId_RecoveryNeeded()
    {
        using var cts = new CancellationTokenSource();
        var http = _factory.CreateDefaultClient(new CancelAfterHeadersHandler(new HttpClientHandler(), cts));
        var store = new CtStore { SaveException = new IOException("disk full") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()), store);
        var owner = WakeMeshApiClient.CreateForTests(
            _factory.CreateClient(),
            new StaticAccessTokenProvider(_factory.Auth.CreateAccessToken()));

        var res = await api.RegisterDeviceAsync("t11-persist-fail", cancellationToken: cts.Token);
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.CredentialPersistFailed, res.Kind);
        Assert.True(res.RecoveryNeeded);
        Assert.NotNull(res.ServerDeviceId);
        Assert.DoesNotContain("disk", res.Message ?? "", StringComparison.OrdinalIgnoreCase);
        // One-time credential still available on Value for local retry — not in Message.
        Assert.NotNull(res.Value);
        Assert.False(string.IsNullOrWhiteSpace(res.Value!.Credential));
        Assert.DoesNotContain(res.Value.Credential, res.Message ?? "");

        var after = await owner.ListDevicesAsync();
        Assert.Contains(after.Value!, d => d.Id == res.ServerDeviceId);
    }

    [Fact]
    public async Task Register_CallerCancelBeforeSend_IsCanceled_NoRecovery()
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

    // --- 4. Typed invalid response + provider exceptions without host string matching ---

    [Fact]
    public async Task MalformedSuccessBody_IsInvalidResponse()
    {
        var handler = new FixedJsonHandler(HttpStatusCode.OK, "{not-json");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        var res = await api.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.InvalidResponse, res.Kind);
        Assert.Equal("invalidJson", res.ErrorCode);
    }

    [Fact]
    public async Task NullSuccessBody_ForReferenceType_IsInvalidResponse()
    {
        var handler = new FixedJsonHandler(HttpStatusCode.OK, "null");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        var res = await api.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.InvalidResponse, res.Kind);
    }

    [Fact]
    public async Task TruncatedSuccessBody_IsInvalidResponse()
    {
        var handler = new FixedJsonHandler(HttpStatusCode.OK, "{\"id\":");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(http, new StaticAccessTokenProvider("t"));
        var res = await api.GetDeviceAsync(Guid.NewGuid());
        Assert.Equal(ApiErrorKind.InvalidResponse, res.Kind);
    }

    [Fact]
    public async Task TokenProviderException_IsTokenProviderFailed_NotHostGuessed()
    {
        using var http = new HttpClient(new FixedJsonHandler(HttpStatusCode.OK, "[]")) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(
            http,
            new ThrowingTokens(new InvalidOperationException("no account for host login.live.com RT-SECRET")));
        var res = await api.ListDevicesAsync();
        Assert.False(res.IsSuccess);
        Assert.Equal(ApiErrorKind.TokenProviderFailed, res.Kind);
        Assert.Equal("tokenProviderFailed", res.ErrorCode);
        Assert.DoesNotContain("login.live.com", res.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RT-SECRET", res.Message ?? "");
    }

    [Fact]
    public async Task GenericTokenProviderException_IsTokenProviderFailed()
    {
        using var http = new HttpClient(new FixedJsonHandler(HttpStatusCode.OK, "[]")) { BaseAddress = new Uri("http://localhost/") };
        var api = WakeMeshApiClient.CreateForTests(
            http,
            new ThrowingTokens(new Exception("MSAL secret refresh_token=RT-SECRET")));
        var res = await api.ListDevicesAsync();
        Assert.Equal(ApiErrorKind.TokenProviderFailed, res.Kind);
        Assert.DoesNotContain("RT-SECRET", res.Message ?? "");
        Assert.DoesNotContain("refresh_token", res.Message ?? "");
    }

    private sealed class ThrowingTokens(Exception ex) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken c = default) => Task.FromException<string?>(ex);
    }

    private sealed class RecordingApplier : IDesiredApplier
    {
        private readonly Func<DesiredSnapshotDto, DesiredApplyResult> _fn;
        public int Calls { get; private set; }
        public RecordingApplier(Func<DesiredSnapshotDto, DesiredApplyResult> fn) => _fn = fn;
        public Task<DesiredApplyResult> ApplyAsync(DesiredSnapshotDto snapshot, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_fn(snapshot));
        }
    }

    private sealed class FixedJsonHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public FixedJsonHandler(HttpStatusCode code, string body)
        {
            _code = code;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(res);
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayingHandler(TimeSpan delay) => _delay = delay;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
