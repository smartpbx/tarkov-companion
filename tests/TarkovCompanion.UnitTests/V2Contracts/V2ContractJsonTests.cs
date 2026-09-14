using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

/// <summary>The canonical options close the three ways default System.Text.Json fails open.</summary>
public sealed class V2ContractJsonTests
{
    private static readonly JsonSerializerOptions Options = V2ContractJson.Options;

    [Fact]
    public void IntegerEnumValuesAreRejected()
    {
        const string json = """{"completeness":3,"freshness":"Current"}""";
        var permissive = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        Assert.Equal(ResultCompleteness.Complete, JsonSerializer.Deserialize<ResultStatus>(json, permissive)!.Completeness);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ResultStatus>(json, Options));
    }

    [Fact]
    public void UndefinedEnumNamesAreRejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ResultStatus>("""{"completeness":"LiveDetection","freshness":"Current"}""", Options));
    }

    [Fact]
    public void MissingIdentifiersAndEnumsDoNotBecomeDefaults()
    {
        var origin = JsonSerializer.Serialize(V2ContractTestData.Origin, Options);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkspaceOrigin>(
            origin.Replace("\"kind\":\"DesktopApplication\",", string.Empty, StringComparison.Ordinal),
            Options));

        var withoutWorkspace = JsonNode.Parse(origin)!.AsObject();
        withoutWorkspace.Remove("workspaceId");
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WorkspaceOrigin>(withoutWorkspace.ToJsonString(), Options));
    }

    [Fact]
    public void NullForARequiredReferenceIsRejected()
    {
        Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<ProducerIdentity>("""{"name":null,"version":"2"}""", Options));
    }

    [Fact]
    public void IdentifiersRoundTripThroughTheirConstructors()
    {
        var json = JsonSerializer.Serialize(V2ContractTestData.Origin, Options);
        var roundTrip = JsonSerializer.Deserialize<WorkspaceOrigin>(json, Options);

        Assert.Equal(V2ContractTestData.Origin, roundTrip);
        Assert.Contains("\"kind\":\"DesktopApplication\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAreReadOnly()
    {
        Assert.True(Options.IsReadOnly);
    }
}
