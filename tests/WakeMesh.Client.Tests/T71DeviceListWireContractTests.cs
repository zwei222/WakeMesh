using System.Text.Json;
using WakeMesh.Client.Models;

namespace WakeMesh.Client.Tests;

/// <summary>
/// T71 wire contract: DeviceListItem carries an optional <c>hasSuccessfulSync</c> projection.
/// The field must stay tri-state on the wire — absent (older API) deserializes to null, which is
/// distinct from an explicit false ("no successful ACK yet"). Legacy 8-argument construction and
/// JSON without the property must keep working.
/// </summary>
public sealed class T71DeviceListWireContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string LegacyJson = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "displayName": "pixel",
          "status": "Active",
          "desiredRevision": 0,
          "appliedRevision": 0,
          "retirementState": "none",
          "cancelConfirmed": false,
          "revokedAt": null
        }
        """;

    [Fact]
    public void LegacyJsonWithoutField_DeserializesToNull_NotFalse()
    {
        var item = JsonSerializer.Deserialize<DeviceListItem>(LegacyJson, Web);

        Assert.NotNull(item);
        Assert.Null(item!.HasSuccessfulSync);
        Assert.Equal(0, item.DesiredRevision);
        Assert.Equal(0, item.AppliedRevision);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ExplicitBoolean_IsPreserved(string raw, bool expected)
    {
        var json = LegacyJson.TrimEnd('}', '\n', '\r', ' ')
            .TrimEnd() + ",\n  \"hasSuccessfulSync\": " + raw + "\n}";

        var item = JsonSerializer.Deserialize<DeviceListItem>(json, Web);

        Assert.NotNull(item);
        Assert.Equal(expected, item!.HasSuccessfulSync);
    }

    [Theory]
    [InlineData(false, JsonValueKind.False)]
    [InlineData(true, JsonValueKind.True)]
    public void SerializedProperty_UsesCamelCaseName(bool value, JsonValueKind expectedKind)
    {
        var item = new DeviceListItem(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "pixel",
            "Active",
            0,
            0,
            "none",
            false,
            null,
            value);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item, Web));
        Assert.True(doc.RootElement.TryGetProperty("hasSuccessfulSync", out var flag));
        Assert.Equal(expectedKind, flag.ValueKind);
    }

    [Fact]
    public void LegacyEightArgumentConstruction_DefaultsToNull()
    {
        // Compile-level guard: existing call sites that predate the projection keep working.
        var item = new DeviceListItem(
            Guid.NewGuid(), "pixel", "Active", 0, 0, "none", false, null);

        Assert.Null(item.HasSuccessfulSync);
    }

    [Fact]
    public void OfflineTimestampIsNotProjected()
    {
        // The contract is a boolean only: no ACK/receipt timestamp is exposed on the list row.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            new DeviceListItem(Guid.NewGuid(), "pixel", "Active", 0, 0, "none", false, null, true), Web));

        foreach (var name in doc.RootElement.EnumerateObject().Select(p => p.Name))
        {
            Assert.DoesNotContain("receipt", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ackAt", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("timestamp", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
