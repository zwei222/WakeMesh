using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WakeMesh.Api.Data;
using WakeMesh.Api.Models;
using WakeMesh.Api.Services;

namespace WakeMesh.Api.Tests;

/// <summary>
/// T214 backend hardening regression (focused, default runner).
///
/// Covers four MUST/OPTIONAL items from the hardening contract:
/// - A: PostgreSQL fail-closed TLS guard outside Development/Test.
/// - B: ACK failure-detail normalization (truncate, never reject) on write; read path untouched.
/// - C: single-owner startup guard (distinct non-blank OIDs &gt; 1 fails unless explicitly opted in).
/// - D: <c>X-Content-Type-Options: nosniff</c> on every response (including error responses).
///
/// All fixtures and credentials here are fictional. No live host, secret, or cloud value appears.
/// Behavioral cases go through the real service / actual API fixture (SQLite in-memory) and the real
/// ASP.NET host; no private evidence fixture is required.
/// </summary>
public sealed class T214SecurityHardeningTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;

    public T214SecurityHardeningTests(ApiFactory factory) => _factory = factory;

    // ---------------------------------------------------------------------
    // B: ACK failure normalization (write path truncates; read path intact)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task T214_Ack_LongMessage_StoredAsExactPrefix512()
    {
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-ack-long-message");
        var message = LongPattern(10_000);

        var res = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "failure",
            failures = new[] { new { scheduleId = (Guid?)null, code = "applyFailed", message } }
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ack = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("accepted", ack.GetProperty("status").GetString());

        var stored = await RecentFailuresAsync(Owner(), id);
        Assert.Single(stored);
        var storedMessage = stored[0].GetProperty("message").GetString();
        Assert.Equal(message[..512], storedMessage);
        Assert.Equal(512, storedMessage!.Length);
    }

    [Fact]
    public async Task T214_Ack_TooManyFailures_StoresFirst64()
    {
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-ack-too-many");
        var failures = Enumerable.Range(0, 70)
            .Select(i => new { scheduleId = (Guid?)null, code = $"c{i}", message = $"m{i}" })
            .ToArray();

        var res = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "failure",
            failures
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = await RecentFailuresAsync(Owner(), id);
        Assert.Equal(64, stored.Count);
        var codes = stored.Select(f => f.GetProperty("code").GetString()).ToArray();
        Assert.Equal(Enumerable.Range(0, 64).Select(i => $"c{i}").ToArray(), codes);
    }

    [Fact]
    public async Task T214_Ack_LongCode_Truncated64()
    {
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-ack-long-code");
        var code = LongPattern(100);

        var res = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "failure",
            failures = new[] { new { scheduleId = (Guid?)null, code, message = "short" } }
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = await RecentFailuresAsync(Owner(), id);
        Assert.Single(stored);
        Assert.Equal(code[..64], stored[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task T214_Ack_SurrogateBoundary_NoLoneSurrogate()
    {
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-ack-surrogate");
        const char high = '\uD83D';
        const char low = '\uDE00';
        // 511 'a' + a full surrogate pair (indices 511 high, 512 low) + tail.
        // The 512-unit cut lands right after the high surrogate, so the guard must drop one more unit.
        var message = new string('a', 511) + high + low + new string('b', 50);

        var res = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "failure",
            failures = new[] { new { scheduleId = (Guid?)null, code = "emoji", message } }
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = await RecentFailuresAsync(Owner(), id);
        Assert.Single(stored);
        var storedMessage = stored[0].GetProperty("message").GetString()!;
        Assert.Equal(new string('a', 511), storedMessage);
        AssertNoLoneSurrogate(storedMessage);
    }

    [Fact]
    public async Task T214_Ack_ExistingLongRowInDb_ReadBackIntact()
    {
        // Seed a pre-existing over-long row directly (as produced before this change) and prove the
        // read path still returns it verbatim: no truncation and no migration.
        var owner = Owner();
        var (id, _) = await RegisterAsync(owner, "t214-ack-existing-long");
        var longMessage = new string('z', 1000);
        var longCode = new string('q', 100);
        var seeded = new List<AckFailureDto>
        {
            new(null, longCode, longMessage),
            new(null, "short", "ok")
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            db.AckRecords.Add(new AckRecordEntity
            {
                Id = Guid.NewGuid(),
                DeviceId = id,
                ReceiptId = Guid.NewGuid(),
                Revision = 0,
                Result = "failure",
                FailuresJson = JsonUtil.Serialize(seeded),
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var stored = await RecentFailuresAsync(owner, id);
        Assert.Equal(2, stored.Count);
        Assert.Equal(longMessage, stored[0].GetProperty("message").GetString());
        Assert.Equal(longCode, stored[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task T214_Ack_SuccessWithFailures_Still400()
    {
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-ack-success-with-failures");
        // More failures than the 64 cap: the rejection must use the ORIGINAL count, never the cap.
        var failures = Enumerable.Range(0, 100)
            .Select(i => new { scheduleId = (Guid?)null, code = $"c{i}", message = "m" })
            .ToArray();

        var res = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "success",
            failures
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("successWithFailures", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // C: single-owner startup guard
    // ---------------------------------------------------------------------

    [Fact]
    public async Task T214_Owner_TwoDistinctOids_FailsStartup_WithoutOidInMessage()
    {
        var settings = new Dictionary<string, string?>
        {
            ["WakeMesh:Auth:AllowedOwnerOids:1"] = TestAuthFixture.OtherOid,
        };

        var caught = await CaptureHostFailureAsync(ApiFactory.CreateFor(extraSettings: settings));
        Assert.NotNull(caught);

        var text = caught!.ToString();
        Assert.Contains("AllowedOwnerOids", text, StringComparison.Ordinal);
        Assert.Contains("AllowMultipleOwners", text, StringComparison.Ordinal);
        Assert.Contains("2", text, StringComparison.Ordinal);
        // Never echo the configured OID values.
        Assert.DoesNotContain(TestAuthFixture.OtherOid, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestAuthFixture.AllowedOid, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T214_Owner_CaseOnlyDuplicate_Starts()
    {
        var settings = new Dictionary<string, string?>
        {
            // Same OID up to case only: OrdinalIgnoreCase makes this a single distinct owner.
            ["WakeMesh:Auth:AllowedOwnerOids:1"] = TestAuthFixture.AllowedOid.ToUpperInvariant(),
        };

        await using var factory = ApiFactory.CreateFor(extraSettings: settings);
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task T214_Owner_TwoOids_WithAllowMultipleOwners_BothAuthorized()
    {
        var settings = new Dictionary<string, string?>
        {
            ["WakeMesh:Auth:AllowedOwnerOids:1"] = TestAuthFixture.OtherOid,
            ["WakeMesh:Auth:AllowMultipleOwners"] = "true",
        };

        await using var factory = ApiFactory.CreateFor(extraSettings: settings);

        // Custom factories do not go through the xUnit fixture lifecycle, so create the schema once.
        await factory.InitializeAsync();

        using var first = factory.CreateClient();
        first.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Auth.CreateAccessToken(oid: TestAuthFixture.AllowedOid));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/alarm-groups")).StatusCode);

        using var second = factory.CreateClient();
        second.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Auth.CreateAccessToken(oid: TestAuthFixture.OtherOid));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/alarm-groups")).StatusCode);
    }

    // ---------------------------------------------------------------------
    // A: PostgreSQL TLS guard (host startup path)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task T214_Host_ProductionPostgres_WithoutVerifyFull_FailsStartup()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 55432,
            Database = "wm_t218_guard",
            Username = "wakemesh",
            Password = "t218-fictional-password",
            SslMode = SslMode.Prefer,
        }.ConnectionString;

        var caught = await CaptureHostFailureAsync(ApiFactory.CreateFor(
            providerOverride: "Postgres",
            connectionStringOverride: connectionString,
            skipDatabaseInitialize: true,
            environmentOverride: "Production"));
        Assert.NotNull(caught);

        var text = caught!.ToString();
        Assert.Contains("VerifyFull", text, StringComparison.Ordinal);
        Assert.DoesNotContain("t218-fictional-password", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T214_Host_ProductionPostgres_VerifyFullWithCa_Builds()
    {
        var ca = TemporaryCaFile.Create();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = 55432,
                Database = "wm_t218_guard",
                Username = "wakemesh",
                Password = "t218-fictional-password",
                SslMode = SslMode.VerifyFull,
                RootCertificate = ca.Path,
            }.ConnectionString;

            await using var factory = ApiFactory.CreateFor(
                providerOverride: "Postgres",
                connectionStringOverride: connectionString,
                skipDatabaseInitialize: true,
                environmentOverride: "Production");

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            Assert.True(db.Database.IsNpgsql());
        }
        finally
        {
            ca.Dispose();
        }
    }

    [Fact]
    public async Task T214_Host_ProductionSqlite_Unaffected()
    {
        await using var factory = ApiFactory.CreateFor(environmentOverride: "Production");
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---------------------------------------------------------------------
    // D: nosniff on every response (success, auth failure, error)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task T214_Nosniff_OnHealthAnd401AndError()
    {
        var health = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("nosniff", ResponseHeader(health, "X-Content-Type-Options"));

        var anon = await _factory.CreateClient().GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal("nosniff", ResponseHeader(anon, "X-Content-Type-Options"));

        // Error response produced by the controller (400 invalidResult) also carries the header.
        var (device, id, receipt, revision) = await NewDeviceWithReceiptAsync("t214-nosniff-error");
        var bad = await device.PostAsJsonAsync(AcksPath(id), new
        {
            receiptId = receipt,
            revision,
            result = "bogus",
            failures = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("nosniff", ResponseHeader(bad, "X-Content-Type-Options"));
    }

    // ---------------------------------------------------------------------
    // A: PostgreSQL TLS guard (pure unit contract)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(SslMode.Disable)]
    [InlineData(SslMode.Prefer)]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCA)]
    public void T214_PgGuard_RejectsNonVerifyFull(SslMode sslMode)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "pooler.invalid",
            Port = 5432,
            Database = "postgres",
            Username = "postgres.fictional",
            Password = "t218-fictional-password",
            SslMode = sslMode,
        }.ConnectionString;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConfiguration.EnsureVerifiedTls(connectionString));
        Assert.Contains("VerifyFull", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t218-fictional-password", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T214_PgGuard_RejectsVerifyFullWithoutRootCertificate()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "pooler.invalid",
            Port = 5432,
            Database = "postgres",
            Username = "postgres.fictional",
            Password = "t218-fictional-password",
            SslMode = SslMode.VerifyFull,
        }.ConnectionString;

        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConfiguration.EnsureVerifiedTls(connectionString));
        Assert.Contains("Root Certificate", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t218-fictional-password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T214_PgGuard_AcceptsVerifyFullWithCa()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "pooler.invalid",
            Port = 5432,
            Database = "postgres",
            Username = "postgres.fictional",
            Password = "t218-fictional-password",
            SslMode = SslMode.VerifyFull,
            RootCertificate = "/fictional/ca/supabase-ca.crt",
        }.ConnectionString;

        // Pure validation: does not read the file, only requires the keyword to be present.
        PostgresConfiguration.EnsureVerifiedTls(connectionString);
    }

    // ---------------------------------------------------------------------
    // B: null handling and published constants
    // ---------------------------------------------------------------------

    [Fact]
    public async Task T214_Ack_NullCodeBecomesEmptyNullMessageStaysNull()
    {
        var owner = Owner();
        var (id, credential) = await RegisterAsync(owner, "t214-ack-null-fields");
        var device = Owner();
        device.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        var snapshot = await device.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{id}/desired");
        var receipt = snapshot.GetProperty("receiptId").GetGuid();
        var revision = snapshot.GetProperty("revision").GetInt64();

        // The HTTP model binder rejects a null Code (non-nullable parameter), so exercise the real
        // service directly: the null -> "" rule is defensive for deserialized/stored payloads.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WakeMeshDbContext>();
            var service = new DesiredAckService(db);
            var request = new AckRequest(
                receipt,
                revision,
                "failure",
                new[] { new AckFailureDto(null, null!, null) },
                null);

            var (status, _) = await service.PostAckAsync(id, credential, request, CancellationToken.None);
            Assert.Equal(200, status);
        }

        var stored = await RecentFailuresAsync(owner, id);
        Assert.Single(stored);
        Assert.Equal(string.Empty, stored[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, stored[0].GetProperty("message").ValueKind);
    }

    [Fact]
    public void T214_Ack_ConstantsMatchContract()
    {
        Assert.Equal(64, DesiredAckService.MaxAckFailures);
        Assert.Equal(64, DesiredAckService.MaxAckFailureCodeLength);
        Assert.Equal(512, DesiredAckService.MaxAckFailureMessageLength);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Auth.CreateAccessToken());
        return client;
    }

    private static async Task<(Guid Id, string Credential)> RegisterAsync(HttpClient owner, string name)
    {
        var res = await owner.PostAsJsonAsync("/api/v1/devices", new { displayName = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("credential").GetString()!);
    }

    private async Task<(HttpClient Device, Guid Id, Guid ReceiptId, long Revision)> NewDeviceWithReceiptAsync(string name)
    {
        var owner = Owner();
        var (id, credential) = await RegisterAsync(owner, name);
        var device = Owner();
        device.DefaultRequestHeaders.Add("X-Device-Credential", credential);
        var snapshot = await device.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{id}/desired");
        return (
            device,
            id,
            snapshot.GetProperty("receiptId").GetGuid(),
            snapshot.GetProperty("revision").GetInt64());
    }

    private static async Task<List<JsonElement>> RecentFailuresAsync(HttpClient owner, Guid id)
    {
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{id}");
        return detail.GetProperty("recentFailures").EnumerateArray().ToList();
    }

    private static string AcksPath(Guid id) => $"/api/v1/devices/{id}/acks";

    private static string LongPattern(int length)
    {
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            builder.Append((char)('a' + (i % 26)));
        }

        return builder.ToString();
    }

    private static void AssertNoLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]), "lone high surrogate stored");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]), "lone low surrogate stored");
            }
        }
    }

    private static string? ResponseHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<Exception?> CaptureHostFailureAsync(ApiFactory factory)
    {
        Exception? failure = null;
        try
        {
            _ = factory.Services;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await factory.DisposeAsync();
        }
        catch
        {
            // The host never started; disposal failures must not mask the captured startup error.
        }

        return failure;
    }

    private sealed class TemporaryCaFile : IDisposable
    {
        private TemporaryCaFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryCaFile Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wakemesh-t218-ca-" + Guid.NewGuid().ToString("N") + ".crt");
            File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nfictional-test-fixture\n-----END CERTIFICATE-----\n");
            return new TemporaryCaFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Best effort cleanup of a temp fixture.
            }
        }
    }
}
