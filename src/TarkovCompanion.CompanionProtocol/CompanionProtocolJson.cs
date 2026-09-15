using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The one fail-closed JSON boundary shared by direct and relayed paired transports.</summary>
public static class CompanionProtocolJson
{
    private static readonly FrozenSet<Type> WireRoots = new[]
    {
        typeof(ClientHello),
        typeof(ServerHello),
        typeof(PairingOffer),
        typeof(PairingNonceReveal),
        typeof(PairingRequest),
        typeof(SessionResumeRequest),
        typeof(HandshakeChallenge),
        typeof(DeviceKeyProof),
        typeof(SessionEstablished),
        typeof(ClientCommandEnvelope),
        typeof(ClientDeliveryAcknowledgement),
        typeof(ServerEnvelope),
        typeof(OpaqueRelayFrame),
        typeof(ReconnectRequest),
        typeof(ReconnectPlan),
    }.ToFrozenSet();

    private static readonly FrozenSet<string> ProhibitedPropertyNames = new[]
    {
        "$type",
        "$id",
        "$ref",
        "typeName",
        "clrType",
        "assemblyQualifiedName",
        "password",
        "passphrase",
        "secret",
        "apiKey",
        "authorization",
        "cookie",
        "sessionToken",
        "shortCode",
        "pairingCode",
        "privateKey",
        "sharedSecret",
        "trafficKey",
        "accessToken",
        "refreshToken",
        "authorizationToken",
        "bearerToken",
        "groupKey",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The exact wire roots, in the order the schema's root <c>oneOf</c> lists them.</summary>
    public static IReadOnlyList<Type> RootTypes { get; } =
    [
        typeof(ClientCommandEnvelope),
        typeof(ClientDeliveryAcknowledgement),
        typeof(ServerEnvelope),
        typeof(OpaqueRelayFrame),
        typeof(ClientHello),
        typeof(ServerHello),
        typeof(PairingOffer),
        typeof(PairingNonceReveal),
        typeof(PairingRequest),
        typeof(SessionResumeRequest),
        typeof(HandshakeChallenge),
        typeof(DeviceKeyProof),
        typeof(SessionEstablished),
        typeof(ReconnectRequest),
        typeof(ReconnectPlan),
    ];

    public static byte[] Serialize<T>(T value)
    {
        RequireWireRoot(typeof(T));
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        ValidateLexicalSafety(payload, MaximumBytes(typeof(T)));
        return payload;
    }

    /// <summary>
    /// Reads one exact root. Every malformed, oversized, ambiguous, or semantically invalid payload
    /// fails with <see cref="JsonException"/>, so a transport has exactly one rejection path.
    /// </summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        RequireWireRoot(typeof(T));
        ValidateLexicalSafety(payload, MaximumBytes(typeof(T)));
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options)
                ?? throw new JsonException("A protocol root cannot be null.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
                                              InvalidOperationException or NotSupportedException or
                                              OverflowException or InvalidCastException or
                                              KeyNotFoundException or IndexOutOfRangeException or
                                              System.Reflection.TargetInvocationException)
        {
            throw new JsonException($"The {typeof(T).Name} payload violates the paired protocol contract.", exception);
        }
    }

    /// <summary>Reads the exact root an authenticated relay payload names.</summary>
    public static object DeserializeRelayPayload(RelayPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = payload.Json.Span;
        return payload.Kind switch
        {
            RelayPayloadKind.ClientCommandEnvelope => Deserialize<ClientCommandEnvelope>(json),
            RelayPayloadKind.ClientDeliveryAcknowledgement => Deserialize<ClientDeliveryAcknowledgement>(json),
            RelayPayloadKind.ReconnectRequest => Deserialize<ReconnectRequest>(json),
            RelayPayloadKind.ServerEnvelope => Deserialize<ServerEnvelope>(json),
            RelayPayloadKind.ReconnectPlan => Deserialize<ReconnectPlan>(json),
            _ => throw new JsonException("Unknown relay payload kind."),
        };
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = ProtocolBounds.MaxJsonDepth,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

            // JSON objects are unordered. A browser client must not have to place "type" first.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));

        // Reused Core DTOs normalize a timestamp to UTC before protocol validation sees it, so the
        // explicit zero offset is enforced where the text is read.
        options.Converters.Add(new ZeroOffsetDateTimeOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static int MaximumBytes(Type root) =>
        root == typeof(OpaqueRelayFrame) ? ProtocolBounds.MaxRelayFrameBytes : ProtocolBounds.MaxPayloadBytes;

    private static void RequireWireRoot(Type type)
    {
        if (!WireRoots.Contains(type))
        {
            throw new InvalidOperationException($"{type.Name} is not an exact paired-protocol wire root.");
        }
    }

    internal static void ValidateLexicalSafety(ReadOnlySpan<byte> payload, int maximumBytes)
    {
        if (payload.Length == 0 || payload.Length > maximumBytes)
        {
            throw new JsonException($"A protocol payload is 1-{maximumBytes} bytes.");
        }

        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolBounds.MaxJsonDepth,
        });
        var containers = new Stack<ContainerFrame>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        CountArrayItem(containers);
                        containers.Push(ContainerFrame.Object());
                        break;
                    case JsonTokenType.StartArray:
                        CountArrayItem(containers);
                        containers.Push(ContainerFrame.Array());
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0)
                        {
                            throw new JsonException("An unexpected container terminator was found.");
                        }

                        containers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (containers.Count == 0 || !containers.Peek().IsObject)
                        {
                            throw new JsonException("A property appeared outside an object.");
                        }

                        var property = reader.GetString() ?? throw new JsonException("A property name cannot be null.");
                        ValidateString(property);
                        if (!containers.Peek().Properties!.Add(property))
                        {
                            throw new JsonException($"Duplicate property '{property}' is ambiguous.");
                        }

                        if (ProhibitedPropertyNames.Contains(property))
                        {
                            throw new JsonException($"Property '{property}' is prohibited on the paired wire.");
                        }

                        break;
                    case JsonTokenType.String:
                        CountArrayItem(containers);
                        ValidateString(reader.GetString() ?? string.Empty);
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        CountArrayItem(containers);
                        break;
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            // Utf8JsonReader reports invalid UTF-8 inside a string token this way.
            throw new JsonException("A protocol payload contains invalid UTF-8.", exception);
        }

        if (containers.Count != 0)
        {
            throw new JsonException("A JSON container was not closed.");
        }
    }

    private static void CountArrayItem(Stack<ContainerFrame> containers)
    {
        if (containers.Count == 0 || containers.Peek().IsObject)
        {
            return;
        }

        var frame = containers.Peek();
        frame.ItemCount++;
        if (frame.ItemCount > ProtocolBounds.MaxCollectionItems)
        {
            throw new JsonException($"An array may contain at most {ProtocolBounds.MaxCollectionItems} items.");
        }
    }

    private static void ValidateString(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > ProtocolBounds.MaxStringBytes)
        {
            throw new JsonException($"A string may contain at most {ProtocolBounds.MaxStringBytes} UTF-8 bytes.");
        }
    }

    private sealed class ContainerFrame
    {
        private ContainerFrame(bool isObject)
        {
            IsObject = isObject;
            Properties = isObject ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        }

        public bool IsObject { get; }

        public HashSet<string>? Properties { get; }

        public int ItemCount { get; set; }

        public static ContainerFrame Object() => new(true);

        public static ContainerFrame Array() => new(false);
    }
}

/// <summary>Reads only timestamps written with a zero UTC offset and writes the serializer's default form.</summary>
internal sealed class ZeroOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !reader.TryGetDateTimeOffset(out var value) ||
            value.Offset != TimeSpan.Zero)
        {
            throw new JsonException("A protocol timestamp is ISO 8601 text with an explicit zero UTC offset.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Guarantees that canonical state the reducer commits can always be delivered. The largest
/// message carrying full state is a command acknowledgement inside a server envelope, so the
/// check serializes exactly that shape with the widest legal envelope and acknowledgement values
/// through the same lexical boundary every transport uses, and keeps
/// <see cref="ProtocolBounds.MaintenanceReserveBytes"/> of headroom for server-time maintenance.
/// </summary>
public static class CanonicalDeliveryBudget
{
    private static readonly CommandId ProbeCommand = new(Guid.Parse("7f000000-0000-4000-8000-000000000001"));
    private static readonly CommandId ProbeChange = new(Guid.Parse("7f000000-0000-4000-8000-000000000002"));
    private static readonly CompanionDeviceId ProbeDevice = new(Guid.Parse("7f000000-0000-4000-8000-000000000003"));
    private static readonly DeviceSessionId ProbeSession = new(Guid.Parse("7f000000-0000-4000-8000-000000000004"));
    private static readonly DateTimeOffset ProbeUtc = new(9999, 12, 31, 23, 59, 59, 999, TimeSpan.Zero);

    /// <summary>True when the state fits with maintenance headroom; the reducer never commits state that does not.</summary>
    public static bool Fits(CanonicalCompanionState state) =>
        Fits(state, ProtocolBounds.MaxPayloadBytes - ProtocolBounds.MaintenanceReserveBytes);

    /// <summary>True when every message carrying the state can still be delivered, without headroom.</summary>
    public static bool IsDeliverable(CanonicalCompanionState state) =>
        Fits(state, ProtocolBounds.MaxPayloadBytes);

    internal static ServerEnvelope ProbeEnvelope(CanonicalCompanionState state)
    {
        // The acknowledgement names another change at the widest revision; a real acknowledgement's
        // values are never wider, its disposition name never longer, and its code never longer.
        var widest = new AggregateRevision(ProtocolBounds.MaxWireInteger);
        var probeState = new CanonicalCompanionState(
            state.AuthorityEpoch,
            state.WorkspaceId,
            state.DesktopInstanceId,
            new GlobalRevision(ProtocolBounds.MaxWireInteger),
            state.DesktopDeviceId,
            state.DeviceModes,
            state.Workspace,
            state.Marks,
            new CaptureIntentAggregate(new AggregateCursor(widest, ProbeChange), state.CaptureIntent.ActiveIntent));
        return new ServerEnvelope(
            new CompanionProtocolVersion(CompanionProtocolVersion.MaxMajor, CompanionProtocolVersion.MaxMinor),
            ProbeSession,
            ProbeDevice,
            ProbeUtc,
            new DeliverySequence(ProtocolBounds.MaxWireInteger),
            new CommandAcknowledgementMessage(new CommandAcknowledgement(
                ProbeCommand,
                CanonicalAggregateKind.CaptureIntent,
                widest,
                widest,
                ProbeChange,
                new GlobalRevision(ProtocolBounds.MaxWireInteger),
                state.AuthorityEpoch,
                CommandDisposition.RequiresSnapshot,
                new string('x', ProtocolBounds.MaxShortStringBytes),
                probeState)));
    }

    private static bool Fits(CanonicalCompanionState state, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            CompanionProtocolJson.ValidateLexicalSafety(
                JsonSerializer.SerializeToUtf8Bytes(ProbeEnvelope(state), CompanionProtocolJson.Options),
                maximumBytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
