using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The one fail-closed JSON boundary shared by direct and relayed paired transports.</summary>
public static class CompanionProtocolJson
{
    private static readonly FrozenSet<Type> WireRoots = new[]
    {
        typeof(ClientHello),
        typeof(ServerHello),
        typeof(PairingRequest),
        typeof(PairingChallenge),
        typeof(PairingProof),
        typeof(ClientCommandEnvelope),
        typeof(ServerEnvelope),
        typeof(OpaqueRelayFrame),
        typeof(ReconnectRequest),
        typeof(ReconnectPlan),
    }.ToFrozenSet();

    private static readonly FrozenSet<string> ProhibitedPropertyNames = new[]
    {
        "$type",
        "typeName",
        "clrType",
        "assemblyQualifiedName",
        "password",
        "shortCode",
        "privateKey",
        "accessToken",
        "refreshToken",
        "authorizationToken",
        "bearerToken",
        "groupKey",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        RequireWireRoot(typeof(T));
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        ValidateLexicalSafety(payload);
        return payload;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        RequireWireRoot(typeof(T));
        ValidateLexicalSafety(payload);
        return JsonSerializer.Deserialize<T>(payload, Options)
            ?? throw new JsonException("A protocol root cannot be null.");
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
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void RequireWireRoot(Type type)
    {
        if (!WireRoots.Contains(type))
        {
            throw new InvalidOperationException($"{type.Name} is not an exact paired-protocol wire root.");
        }
    }

    private static void ValidateLexicalSafety(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > ProtocolBounds.MaxPayloadBytes)
        {
            throw new JsonException($"A protocol payload is 1-{ProtocolBounds.MaxPayloadBytes} bytes.");
        }

        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolBounds.MaxJsonDepth,
        });
        var containers = new Stack<ContainerFrame>();
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
