using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class GoldenAndHostileJsonTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(SchemaNode()));

    public static TheoryData<string> GoldenWireFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in WireFilePaths())
        {
            data.Add(file);
        }

        return data;
    }

    [Fact]
    public void EveryWireRootHasGoldenCoverage()
    {
        var covered = WireFilePaths().Select(file => RootFor(file)).ToHashSet();

        Assert.True(covered.SetEquals(CompanionProtocolJson.RootTypes), string.Join(", ", covered.Select(type => type.Name)));
        Assert.Equal(15, CompanionProtocolJson.RootTypes.Distinct().Count());
    }

    [Fact]
    public void EveryClosedCommandAndCanonicalAggregateHasAGoldenDiscriminator()
    {
        var commandTypes = WireFilePaths()
            .Where(file => file.StartsWith("commands/", StringComparison.Ordinal))
            .Select(file => JsonNode.Parse(Golden(file))!["command"]!["type"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var expectedCommands = typeof(CompanionCommand)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => (string)attribute.TypeDiscriminator!)
            .ToHashSet(StringComparer.Ordinal);
        var updateTypes = WireFilePaths()
            .Where(file => file.StartsWith("server/canonical-update-", StringComparison.Ordinal))
            .Select(file => JsonNode.Parse(Golden(file))!["message"]!["update"]!["type"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var expectedUpdates = typeof(CanonicalUpdate)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => (string)attribute.TypeDiscriminator!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(commandTypes.SetEquals(expectedCommands), string.Join(", ", commandTypes));
        Assert.True(updateTypes.SetEquals(expectedUpdates), string.Join(", ", updateTypes));
    }

    [Theory]
    [MemberData(nameof(GoldenWireFiles))]
    public void GoldenVectorsRoundTripThroughTheirExactRootAndValidateStrictlyAgainstTheSchema(string file)
    {
        var root = RootFor(file);
        var payload = Golden(file);
        var node = JsonNode.Parse(payload);

        AssertJsonEqual(payload, Reserialize(root, payload));
        Assert.Empty(Validator.Value.Validate(node));
        Assert.Empty(Validator.Value.ValidateStrict(Definition(root), node));
    }

    [Fact]
    public void SchemaRootsDiscriminatorsAndEnumsMatchTheClosedCSharpModel()
    {
        var schema = SchemaNode();
        var definitions = schema["$defs"]!.AsObject();

        Assert.Empty(Validator.Value.UnresolvedReferences());
        Assert.Equal(
            CompanionProtocolJson.RootTypes.Select(Definition),
            schema["oneOf"]!.AsArray().Select(item => item!["$ref"]!.GetValue<string>()["#/$defs/".Length..]));

        AssertDiscriminators<CompanionCommand>(definitions, "command");
        AssertDiscriminators<ServerMessage>(definitions, "serverMessage");
        AssertDiscriminators<CanonicalUpdate>(definitions, "canonicalUpdate");
        AssertDiscriminators<WorkspaceAction>(definitions, "workspaceAction");
        AssertDiscriminators<ProfilePreferenceMutation>(definitions, "profilePreferenceMutation");

        AssertEnum<CommandDisposition>(definitions["commandAcknowledgement"]!["properties"]!["disposition"]!);
        AssertEnum<ReconnectDisposition>(definitions["reconnectPlan"]!["properties"]!["disposition"]!);
        AssertEnum<CompatibilityDisposition>(definitions["serverHello"]!["properties"]!["disposition"]!);
        AssertEnum<HandshakePurpose>(definitions["handshakeChallenge"]!["properties"]!["purpose"]!);
        AssertEnum<CompanionInteractionMode>(definitions["deviceModeEntry"]!["properties"]!["mode"]!);
        Assert.Equal(
            PairedScanIntents.Allowed.Select(intent => intent.ToString()).Order(StringComparer.Ordinal).ToArray(),
            definitions["pairedScanIntent"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        AssertEnum<TarkovCompanion.Core.Abstractions.V2.WorkspaceOriginKind>(definitions["workspaceOrigin"]!["properties"]!["kind"]!);
        Assert.Equal(
            TarkovCompanion.Core.Abstractions.V2.MapMarkState.MaxLabelLength,
            definitions["mapMarkState"]!["properties"]!["label"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ProtocolBounds.MaxWireInteger, definitions["positiveRevision"]!["properties"]!["value"]!["maximum"]!.GetValue<long>());
        Assert.Equal(ProtocolBounds.MaxRelayPlaintextBytes, definitions["opaqueRelayFrame"]!["properties"]!["ciphertextLength"]!["maximum"]!.GetValue<int>());
        AssertEnum<ContextualCaptureProgressPhase>(definitions["captureProgressPhase"]!);
        AssertEnum<CaptureCorrectionKind>(definitions["captureCorrectionKind"]!);
        AssertEnum<CanonicalAggregateKind>(definitions["aggregateAcknowledgement"]!["properties"]!["aggregate"]!);
        Assert.Equal(
            Enum.GetNames<TarkovCompanion.Core.Domain.Profiles.ProfileGameMode>()
                .Where(name => name != nameof(TarkovCompanion.Core.Domain.Profiles.ProfileGameMode.Unknown))
                .Order(StringComparer.Ordinal),
            definitions["preferenceProfileContext"]!["properties"]!["mode"]!["enum"]!.AsArray()
                .Select(item => item!.GetValue<string>())
                .Order(StringComparer.Ordinal));
        AssertEnum<ProtectedItemSelectorKind>(definitions["protectedItemRule"]!["properties"]!["selectorKind"]!);
        AssertEnum<ProtectedItemDisposition>(definitions["protectedItemRule"]!["properties"]!["disposition"]!);
        AssertEnum<RecommendationOverrideAction>(definitions["recommendationOverride"]!["properties"]!["action"]!);
        AssertEnum<SharedPersonalizationKind>(definitions["sharedPersonalization"]!["properties"]!["kind"]!);
        Assert.Equal(
            PreferenceSchemaVersion.Current.Major,
            definitions["canonicalProfilePreferencesDocument"]!["properties"]!["schemaVersion"]!["properties"]!["major"]!["const"]!.GetValue<int>());
        Assert.Equal(
            PreferenceSchemaVersion.Current.Minor,
            definitions["canonicalProfilePreferencesDocument"]!["properties"]!["schemaVersion"]!["properties"]!["minor"]!["const"]!.GetValue<int>());
        Assert.DoesNotContain("GroupProtocol", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReducerOutputsForEveryAggregateValidateStrictlyAgainstTheSchema()
    {
        var captured = CanonicalStateMachineTests.PublishedCapture(ScreenshotProvenance(Now.AddSeconds(2)));
        var reviewAt = Now.AddSeconds(3);
        var reviewed = Apply(
            captured.State,
            new ReviewCaptureResultCommand(Command(43), new AggregateRevision(4), reviewAt, reviewAt.AddSeconds(30), Capture(1), CaptureReviewDisposition.NeedsCorrection, "wrong item"),
            TabletContext(reviewAt));
        var corrected = Apply(
            reviewed.State,
            new CorrectCaptureResultCommand(Command(44), new AggregateRevision(5), reviewAt, reviewAt.AddSeconds(30), Capture(1), CaptureCorrectionKind.ItemIdentity, "loot.items.0", "item-2", null),
            TabletContext(reviewAt));
        var pending = Apply(
            corrected.State,
            new RequestControlCommand(Command(45), new AggregateRevision(1), reviewAt, reviewAt.AddMinutes(1), TimeSpan.FromMinutes(2)),
            TabletContext(reviewAt));
        var marked = Apply(pending.State, Upsert(46, 1, 0, reviewAt, kind: MapMarkKind.Ping), TabletContext(reviewAt));
        var conflict = Apply(marked.State, Upsert(47, 1, 0, reviewAt, mark: 2), TabletContext(reviewAt));
        var reuse = Apply(marked.State, Upsert(46, 2, 1, reviewAt, kind: MapMarkKind.Ping, x: 9), TabletContext(reviewAt));
        var unsupported = DesktopCanonicalStateMachine.Apply(
            marked.State,
            Envelope(Upsert(48, 2, 1, reviewAt), version: new CompanionProtocolVersion(2, 7)),
            TabletContext(reviewAt));
        var preferences = Apply(
            marked.State,
            new ActivateProfilePreferencesCommand(
                Command(49),
                new AggregateRevision(1),
                reviewAt,
                reviewAt.AddSeconds(30),
                Preferences()),
            DesktopContext(reviewAt));
        Assert.Equal(CommandDisposition.RejectedCommandIdReuse, reuse.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.UnsupportedVersion, unsupported.Acknowledgement.Disposition);

        var messages = new ServerMessage[]
        {
            new CanonicalUpdateMessage(corrected.Update!),
            new CanonicalUpdateMessage(pending.Update!),
            new CanonicalUpdateMessage(marked.Update!),
            new CanonicalUpdateMessage(preferences.Update!),
            new CommandAcknowledgementMessage(conflict.Acknowledgement),
            new CommandAcknowledgementMessage(reuse.Acknowledgement),
            new CommandAcknowledgementMessage(unsupported.Acknowledgement),
            new CanonicalSnapshotMessage(marked.State),
        };
        for (var index = 0; index < messages.Length; index++)
        {
            var payload = CompanionProtocolJson.Serialize(new ServerEnvelope(
                CompanionProtocolVersion.Current,
                TabletSession,
                TabletDevice,
                reviewAt,
                new DeliverySequence(index + 1),
                messages[index]));
            Assert.Empty(Validator.Value.ValidateStrict("serverEnvelope", JsonNode.Parse(payload)));
        }
    }

    [Theory]
    [MemberData(nameof(GoldenWireFiles))]
    public void StructuredHostileMutationsFailOnlyWithJsonException(string file)
    {
        var root = RootFor(file);
        var rejected = 0;
        var mutations = 0;
        foreach (var mutated in Mutations(JsonNode.Parse(Golden(file))!).Take(600))
        {
            mutations++;
            var exception = Record.Exception(() => Parse(root, Encoding.UTF8.GetBytes(mutated)));
            if (exception is not null)
            {
                Assert.True(exception is JsonException, $"{file}: {exception.GetType().Name} for {mutated}");
                rejected++;
            }
        }

        Assert.InRange(rejected, 1, mutations);
    }

    [Theory]
    [MemberData(nameof(GoldenWireFiles))]
    public void UnknownOptionalFieldsFromANewerMinorRemainReadable(string file)
    {
        var node = JsonNode.Parse(Golden(file))!.AsObject();
        node["futureOptionalField"] = new JsonObject { ["addedInMinor"] = 1 };

        Assert.IsType(RootFor(file), Parse(RootFor(file), Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void UnknownDiscriminatorsEnumMembersAndIntegerEnumsFailClosedButPropertyOrderDoesNot()
    {
        var golden = Encoding.UTF8.GetString(Golden("commands/set-interaction-mode.json"));
        var plan = Encoding.UTF8.GetString(Golden("reconnect/reconnect-plan-up-to-date.json"));

        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(golden.Replace("\"setInteractionMode\"", "\"replayIndependentView\"", StringComparison.Ordinal))));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(golden.Replace("\"Independent\"", "\"Spectate\"", StringComparison.Ordinal))));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(golden.Replace("\"Independent\"", "4", StringComparison.Ordinal))));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ReconnectPlan>(
            Encoding.UTF8.GetBytes(plan.Replace("\"UpToDate\"", "\"PartialReplay\"", StringComparison.Ordinal))));

        var node = JsonNode.Parse(golden)!.AsObject();
        var command = node["command"]!.AsObject();
        var type = command["type"]!.GetValue<string>();
        command.Remove("type");
        command["type"] = type;
        var reordered = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString()));

        Assert.IsType<SetInteractionModeCommand>(reordered.Command);
    }

    [Theory]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://companion.example/objective/objective-1")]
    [InlineData("TARKOV-COMPANION://objective/objective-1")]
    [InlineData("tarkov-companion://objective")]
    [InlineData("tarkov-companion://objective/../../settings")]
    [InlineData("tarkov-companion://objective/objective-1?run=1")]
    [InlineData("tarkov-companion://objective/objective%201")]
    public void SelectionDeepLinksAreACompanionGrammarNeverAShellFileOrWebUri(string link)
    {
        var node = GoldenNode("commands/update-desktop-workspace.json");
        node["command"]!["projection"]!["selection"]!["originDeepLink"] = link;

        Assert.Throws<ArgumentException>(() => new WorkspaceSelection(WorkspaceSelectionKind.Objective, "objective-1", link, null));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString())));
        Assert.NotEmpty(Validator.Value.Validate(node));
        Assert.Equal(
            "tarkov-companion://objective/objective-1",
            new WorkspaceSelection(WorkspaceSelectionKind.Objective, "objective-1", "tarkov-companion://objective/objective-1", null).OriginDeepLink);

        // .NET's "$" also matches before a final newline; the boundary anchors at the absolute end.
        Assert.Throws<ArgumentException>(() => new WorkspaceSelection(WorkspaceSelectionKind.Objective, "objective-1", "tarkov-companion://objective/objective-1\n", null));
    }

    [Fact]
    public void TimestampsInReusedCoreDtosNeedAnExplicitZeroOffsetAndCoreDtosSerializeAsInCore()
    {
        var mark = GoldenNode("commands/upsert-mark.json");
        mark["command"]!["mark"]!["state"]!["expiresUtc"] = "2026-09-14T22:00:30+02:00";
        var intent = GoldenNode("server/canonical-update-capture-intent.json");
        intent["message"]!["update"]!["state"]!["activeIntent"]!["state"]!["armedUtc"] = "2026-09-14T22:00:40+02:00";
        var zulu = GoldenNode("commands/upsert-mark.json");
        zulu["command"]!["mark"]!["state"]!["expiresUtc"] = "2026-09-14T20:00:30Z";

        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(mark.ToJsonString())));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ServerEnvelope>(Encoding.UTF8.GetBytes(intent.ToJsonString())));
        Assert.NotNull(CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(zulu.ToJsonString())));

        var update = GoldenRoot<ServerEnvelope>("server/canonical-update-marks.json");
        var marks = Assert.IsType<MarksCanonicalUpdate>(Assert.IsType<CanonicalUpdateMessage>(update.Message).Update);
        object[] coreValues =
        [
            marks.State.Marks[0].State,
            marks.Origin,
            marks.ContractVersion,
            GoldenRoot<ServerEnvelope>("server/canonical-update-capture-intent.json").Message is CanonicalUpdateMessage { Update: CaptureCanonicalUpdate capture }
                ? capture.State.ActiveIntent!.State
                : throw new InvalidOperationException("The capture golden carries an intent."),
        ];
        foreach (var value in coreValues)
        {
            Assert.Equal(
                JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), TarkovCompanion.Core.Abstractions.V2.V2ContractJson.Options),
                JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), CompanionProtocolJson.Options));
        }
    }

    [Fact]
    public void ANonQueueableCommandCannotSmuggleAnOfflinePreview()
    {
        var node = GoldenNode("commands/set-interaction-mode.json");
        node["command"]!["offlineQueuePreview"] = GoldenNode("commands/show-on-desktop-offline.json")["command"]!["offlineQueuePreview"]!.DeepClone();

        var envelope = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString()));

        Assert.Null(envelope.Command.OfflineQueuePreview);
    }

    [Theory]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","ClientInstanceId":"b","optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"$type":"System.IO.FileInfo"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"shortCode":"123456"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"privateKey":"abc"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],"sharedSecret":"abc"}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[],}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":0},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a",/*c*/"optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":00},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":NaN},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":"0"},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[]}""")]
    [InlineData("""{"supportedVersions":{"minimum":{"major":2,"minor":1},"maximum":{"major":2,"minor":0}},"clientInstanceId":"a","optionalFeatures":[]}""")]
    [InlineData("null")]
    public void AmbiguousCredentialLikeOrMalformedPayloadsAreRejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void PayloadStringCollectionDepthAndEncodingBoundsAreEnforcedBeforeBinding()
    {
        const string helloPrefix =
            "{\"supportedVersions\":{\"minimum\":{\"major\":2,\"minor\":0},\"maximum\":{\"major\":2,\"minor\":0}},\"clientInstanceId\":\"";
        var longString = helloPrefix + new string('x', ProtocolBounds.MaxStringBytes + 1) + "\",\"optionalFeatures\":[]}";
        var longArray = helloPrefix + "a\",\"optionalFeatures\":[" + string.Join(',', Enumerable.Repeat("\"x\"", ProtocolBounds.MaxCollectionItems + 1)) + "]}";
        var deep = helloPrefix + "a\",\"optionalFeatures\":[],\"extra\":" +
                   string.Concat(Enumerable.Repeat("{\"x\":", ProtocolBounds.MaxJsonDepth + 1)) +
                   "0" + string.Concat(Enumerable.Repeat("}", ProtocolBounds.MaxJsonDepth + 1)) + "}";
        byte[] invalidUtf8 = [.. Encoding.UTF8.GetBytes(helloPrefix), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\",\"optionalFeatures\":[]}")];
        var oversized = Encoding.UTF8.GetBytes(helloPrefix + "a\",\"optionalFeatures\":[],\"padding\":\"" +
                                               string.Concat(Enumerable.Repeat(new string('p', 1000) + "\",\"p\":\"", 70)) + "\"}");

        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(longString)));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(longArray)));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(Encoding.UTF8.GetBytes(deep)));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(invalidUtf8));
        Assert.ThrowsAny<JsonException>(() => CompanionProtocolJson.Deserialize<ClientHello>(oversized));
    }

    [Fact]
    public void OnlyExactWireRootsCanBeSerializedOrRead()
    {
        Assert.Throws<InvalidOperationException>(() => CompanionProtocolJson.Serialize(new { arbitrary = true }));
        Assert.Throws<InvalidOperationException>(() => CompanionProtocolJson.Serialize(InitialState()));
        Assert.Throws<InvalidOperationException>(() => CompanionProtocolJson.Deserialize<CanonicalCompanionState>("{}"u8));
        Assert.Throws<InvalidOperationException>(() => CompanionProtocolJson.Deserialize<CompanionCommand>("{}"u8));
    }

    [Fact]
    public void DeterministicHostileByteFuzzAlwaysFailsClosed()
    {
        var random = new Random(276);
        foreach (var root in CompanionProtocolJson.RootTypes)
        {
            for (var iteration = 0; iteration < 400; iteration++)
            {
                var payload = new byte[random.Next(1, 256)];
                random.NextBytes(payload);
                var exception = Record.Exception(() => Parse(root, payload));
                Assert.NotNull(exception);
                Assert.IsAssignableFrom<JsonException>(exception);
            }
        }
    }

    private static string[] WireFilePaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Golden");
        return Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("crypto/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> Mutations(JsonNode document)
    {
        var replacements = new Func<JsonNode?>[]
        {
            () => null,
            () => new JsonObject(),
            () => new JsonArray(),
            () => JsonValue.Create("hostile"),
            () => JsonValue.Create(-1),
            () => JsonValue.Create(9_223_372_036_854_775_807L),
            () => JsonValue.Create(1.5),
            () => JsonValue.Create(true),
            () => JsonValue.Create(new string('z', 900)),
        };

        foreach (var path in Paths(document, []))
        {
            var removed = document.DeepClone();
            if (Remove(removed, path))
            {
                yield return removed.ToJsonString();
            }

            foreach (var replacement in replacements)
            {
                var replaced = document.DeepClone();
                if (Replace(replaced, path, replacement()))
                {
                    yield return replaced.ToJsonString();
                }
            }
        }
    }

    private static IEnumerable<object[]> Paths(JsonNode? node, object[] prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    object[] path = [.. prefix, property.Key];
                    yield return path;
                    foreach (var child in Paths(property.Value, path))
                    {
                        yield return child;
                    }
                }

                break;
            case JsonArray array:
                for (var index = 0; index < Math.Min(array.Count, 2); index++)
                {
                    object[] path = [.. prefix, index];
                    yield return path;
                    foreach (var child in Paths(array[index], path))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }

    private static JsonNode? Parent(JsonNode root, object[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path[..^1])
        {
            current = segment is string name ? current?[name] : current?[(int)segment];
        }

        return current;
    }

    private static bool Remove(JsonNode root, object[] path) => (Parent(root, path), path[^1]) switch
    {
        (JsonObject obj, string name) => obj.Remove(name),
        (JsonArray array, int index) => RemoveAt(array, index),
        _ => false,
    };

    private static bool RemoveAt(JsonArray array, int index)
    {
        array.RemoveAt(index);
        return true;
    }

    private static bool Replace(JsonNode root, object[] path, JsonNode? value)
    {
        switch (Parent(root, path), path[^1])
        {
            case (JsonObject obj, string name):
                obj[name] = value;
                return true;
            case (JsonArray array, int index):
                array[index] = value;
                return true;
            default:
                return false;
        }
    }

    private static void AssertDiscriminators<TBase>(JsonObject definitions, string union)
    {
        var expected = typeof(TBase)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => (string)attribute.TypeDiscriminator!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = definitions[union]!["oneOf"]!.AsArray()
            .Select(item => definitions[item!["$ref"]!.GetValue<string>()["#/$defs/".Length..]]!["properties"]!["type"]!["const"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static void AssertEnum<TEnum>(JsonNode schemaEnum)
        where TEnum : struct, Enum =>
        Assert.Equal(
            Enum.GetNames<TEnum>().Order(StringComparer.Ordinal),
            schemaEnum["enum"]!.AsArray().Where(item => item is not null).Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal));

    private static string Definition(Type root) => char.ToLowerInvariant(root.Name[0]) + root.Name[1..];

    private static Type RootFor(string file) => file switch
    {
        _ when file.StartsWith("commands/", StringComparison.Ordinal) => typeof(ClientCommandEnvelope),
        _ when file.StartsWith("client/", StringComparison.Ordinal) => typeof(ClientDeliveryAcknowledgement),
        _ when file.StartsWith("server/", StringComparison.Ordinal) => typeof(ServerEnvelope),
        _ when file.StartsWith("relay/", StringComparison.Ordinal) => typeof(OpaqueRelayFrame),
        "hello/client-hello.json" => typeof(ClientHello),
        _ when file.StartsWith("hello/server-hello", StringComparison.Ordinal) => typeof(ServerHello),
        "handshake/pairing-offer.json" => typeof(PairingOffer),
        "handshake/pairing-nonce-reveal.json" => typeof(PairingNonceReveal),
        "handshake/pairing-request.json" => typeof(PairingRequest),
        "handshake/session-resume-request.json" => typeof(SessionResumeRequest),
        _ when file.EndsWith("-challenge.json", StringComparison.Ordinal) => typeof(HandshakeChallenge),
        _ when file.EndsWith("-proof.json", StringComparison.Ordinal) => typeof(DeviceKeyProof),
        _ when file.EndsWith("-established.json", StringComparison.Ordinal) => typeof(SessionEstablished),
        _ when file.StartsWith("reconnect/reconnect-request", StringComparison.Ordinal) => typeof(ReconnectRequest),
        _ when file.StartsWith("reconnect/reconnect-plan", StringComparison.Ordinal) => typeof(ReconnectPlan),
        _ => throw new ArgumentOutOfRangeException(nameof(file), file, "A golden file must map to one wire root."),
    };

    private static object Parse(Type root, byte[] payload) => root.Name switch
    {
        nameof(ClientCommandEnvelope) => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(payload),
        nameof(ClientDeliveryAcknowledgement) => CompanionProtocolJson.Deserialize<ClientDeliveryAcknowledgement>(payload),
        nameof(ServerEnvelope) => CompanionProtocolJson.Deserialize<ServerEnvelope>(payload),
        nameof(OpaqueRelayFrame) => CompanionProtocolJson.Deserialize<OpaqueRelayFrame>(payload),
        nameof(ClientHello) => CompanionProtocolJson.Deserialize<ClientHello>(payload),
        nameof(ServerHello) => CompanionProtocolJson.Deserialize<ServerHello>(payload),
        nameof(PairingOffer) => CompanionProtocolJson.Deserialize<PairingOffer>(payload),
        nameof(PairingNonceReveal) => CompanionProtocolJson.Deserialize<PairingNonceReveal>(payload),
        nameof(PairingRequest) => CompanionProtocolJson.Deserialize<PairingRequest>(payload),
        nameof(SessionResumeRequest) => CompanionProtocolJson.Deserialize<SessionResumeRequest>(payload),
        nameof(HandshakeChallenge) => CompanionProtocolJson.Deserialize<HandshakeChallenge>(payload),
        nameof(DeviceKeyProof) => CompanionProtocolJson.Deserialize<DeviceKeyProof>(payload),
        nameof(SessionEstablished) => CompanionProtocolJson.Deserialize<SessionEstablished>(payload),
        nameof(ReconnectRequest) => CompanionProtocolJson.Deserialize<ReconnectRequest>(payload),
        nameof(ReconnectPlan) => CompanionProtocolJson.Deserialize<ReconnectPlan>(payload),
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };

    private static byte[] Reserialize(Type root, byte[] payload) => Parse(root, payload) switch
    {
        ClientCommandEnvelope value => CompanionProtocolJson.Serialize(value),
        ClientDeliveryAcknowledgement value => CompanionProtocolJson.Serialize(value),
        ServerEnvelope value => CompanionProtocolJson.Serialize(value),
        OpaqueRelayFrame value => CompanionProtocolJson.Serialize(value),
        ClientHello value => CompanionProtocolJson.Serialize(value),
        ServerHello value => CompanionProtocolJson.Serialize(value),
        PairingOffer value => CompanionProtocolJson.Serialize(value),
        PairingNonceReveal value => CompanionProtocolJson.Serialize(value),
        PairingRequest value => CompanionProtocolJson.Serialize(value),
        SessionResumeRequest value => CompanionProtocolJson.Serialize(value),
        HandshakeChallenge value => CompanionProtocolJson.Serialize(value),
        DeviceKeyProof value => CompanionProtocolJson.Serialize(value),
        SessionEstablished value => CompanionProtocolJson.Serialize(value),
        ReconnectRequest value => CompanionProtocolJson.Serialize(value),
        ReconnectPlan value => CompanionProtocolJson.Serialize(value),
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };
}
