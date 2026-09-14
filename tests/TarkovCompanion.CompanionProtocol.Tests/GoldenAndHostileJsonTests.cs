using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class GoldenAndHostileJsonTests
{
    [Fact]
    public void ClientEnvelopeMatchesGoldenVector()
    {
        RoundTripGolden<ClientCommandEnvelope>("client-set-mode.json");
    }

    [Fact]
    public void ServerEnvelopeMatchesGoldenVector()
    {
        RoundTripGolden<ServerEnvelope>("server-acknowledgement.json");
    }

    [Fact]
    public void RelayFrameMatchesGoldenVectorAndContainsNoPlaintextState()
    {
        var payload = Golden("opaque-relay-frame.json");
        var frame = CompanionProtocolJson.Deserialize<OpaqueRelayFrame>(payload);
        var roundTrip = Encoding.UTF8.GetString(CompanionProtocolJson.Serialize(frame));

        Assert.DoesNotContain("mapId", roundTrip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("captureIntent", roundTrip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coordinate", roundTrip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selection", roundTrip, StringComparison.OrdinalIgnoreCase);
        AssertJsonEqual(payload, Encoding.UTF8.GetBytes(roundTrip));
    }

    [Fact]
    public void SchemaEnumeratesEveryClosedCommandDiscriminator()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schemas", "v2", "companion-protocol.schema.json"));
        var discriminators = new[]
        {
            "setInteractionMode",
            "requestControl",
            "resolveControl",
            "preemptControl",
            "controlWorkspace",
            "showOnDesktop",
            "upsertMark",
            "deleteMark",
            "requestCaptureIntent",
            "reportCaptureProgress",
            "publishCaptureResult",
            "reviewCaptureResult",
            "correctCaptureResult",
        };

        Assert.All(discriminators, discriminator => Assert.Contains($"\"{discriminator}\"", schema, StringComparison.Ordinal));
        Assert.DoesNotContain("GroupProtocol", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaEnumeratesEveryWireRootAndClosedServerDiscriminator()
    {
        var schema = JsonNode.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Schemas", "v2", "companion-protocol.schema.json")))!;
        var rootReferences = schema["oneOf"]!.AsArray()
            .Select(item => item!["$ref"]!.GetValue<string>().Split('/')[^1])
            .ToArray();
        var serverReferences = schema["$defs"]!["serverMessage"]!["oneOf"]!.AsArray()
            .Select(item => item!["$ref"]!.GetValue<string>().Split('/')[^1])
            .ToArray();

        Assert.Equal(
            new[]
            {
                "clientCommandEnvelope",
                "serverEnvelope",
                "opaqueRelayFrame",
                "clientHello",
                "serverHello",
                "pairingRequest",
                "pairingChallenge",
                "pairingProof",
                "reconnectRequest",
                "reconnectPlan",
            },
            rootReferences);
        Assert.Equal(
            new[]
            {
                "commandAcknowledgementMessage",
                "canonicalSnapshotMessage",
                "canonicalUpdateMessage",
                "deprecationMessage",
            },
            serverReferences);
    }

    [Theory]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","ClientInstanceId":"b","optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"$type":"System.IO.FileInfo"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"shortCode":"123456"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"privateKey":"abc"}""")]
    public void DuplicateClrAndCredentialLikePropertiesAreRejected(string json)
    {
        Assert.Throws<JsonException>(() =>
            CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void UnknownDiscriminatorAndIntegerEnumsAreRejected()
    {
        var golden = Encoding.UTF8.GetString(Golden("client-set-mode.json"));
        Assert.Throws<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(golden.Replace("setInteractionMode", "arbitraryMutation", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(golden.Replace("\"Independent\"", "4", StringComparison.Ordinal))));
    }

    [Fact]
    public void MissingRequiredFieldsAndNullReferencesAreRejected()
    {
        var node = JsonNode.Parse(Golden("client-set-mode.json"))!.AsObject();
        node.Remove("sessionId");
        Assert.ThrowsAny<Exception>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        node = JsonNode.Parse(Golden("client-set-mode.json"))!.AsObject();
        node["command"] = null;
        Assert.ThrowsAny<Exception>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void PayloadStringCollectionAndDepthBoundsAreEnforcedBeforeBinding()
    {
        const string helloPrefix =
            "{\"supportedVersions\":{\"minimum\":{\"major\":2,\"minor\":0},\"maximum\":{\"major\":2,\"minor\":0}},\"clientInstanceId\":\"";
        var tooLong = new string('x', ProtocolBounds.MaxStringBytes + 1);
        var longJson = helloPrefix + tooLong + "\",\"optionalFeatures\":[]}";
        Assert.Throws<JsonException>(() =>
            CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(longJson)));

        var items = string.Join(',', Enumerable.Repeat("\"x\"", ProtocolBounds.MaxCollectionItems + 1));
        var arrayJson = helloPrefix + "a\",\"optionalFeatures\":[" + items + "]}";
        Assert.Throws<JsonException>(() =>
            CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(arrayJson)));

        var deep = "{\"supportedVersions\":{\"minimum\":{\"major\":2,\"minor\":0},\"maximum\":{\"major\":2,\"minor\":0}},\"clientInstanceId\":\"a\",\"optionalFeatures\":[],\"extra\":" +
                   string.Concat(Enumerable.Repeat("{\"x\":", ProtocolBounds.MaxJsonDepth + 1)) +
                   "0" + string.Concat(Enumerable.Repeat("}", ProtocolBounds.MaxJsonDepth + 1)) + "}";
        Assert.ThrowsAny<JsonException>(() =>
            CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(deep)));
    }

    [Fact]
    public void NonFiniteCoordinatesAndExactRootEscapeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapCoordinate(
            "customs",
            null,
            CoordinateSpaceKind.World,
            "v1",
            double.NaN,
            null,
            1));
        Assert.Throws<InvalidOperationException>(() => CompanionProtocolJson.Serialize(new { arbitrary = true }));
    }

    [Fact]
    public void DeterministicHostileByteFuzzAlwaysFailsClosed()
    {
        var random = new Random(276);
        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var payload = new byte[random.Next(1, 256)];
            random.NextBytes(payload);
            var exception = Record.Exception(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(payload));
            Assert.NotNull(exception);
        }
    }

    private static void RoundTripGolden<T>(string name)
    {
        var payload = Golden(name);
        var model = CompanionProtocolJson.Deserialize<T>(payload);
        var serialized = CompanionProtocolJson.Serialize(model);
        AssertJsonEqual(payload, serialized);
    }

    private static byte[] Golden(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    private static void AssertJsonEqual(byte[] expected, byte[] actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected {Encoding.UTF8.GetString(expected)}{Environment.NewLine}Actual {Encoding.UTF8.GetString(actual)}");
}
