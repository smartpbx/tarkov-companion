using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>A SHA-256 digest of one normalized, typed command payload.</summary>
public readonly record struct CommandFingerprint
{
    [JsonConstructor]
    public CommandFingerprint(string value) => Value = ProtocolGuard.Base64Url(
        value,
        nameof(value),
        ProtocolBounds.MaxShortStringBytes,
        exactDecodedBytes: 32);

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The reducer's semantic idempotency identity for a command id. A retry is a duplicate only when
/// the same authenticated device resends a command whose fingerprint is identical; any other
/// payload under a retained id is rejected as identifier reuse instead of being acknowledged as
/// the change that already landed.
/// </summary>
/// <remarks>
/// The command is first serialized through the protocol's closed polymorphic model, so the
/// discriminator and every wire field (including revision, lifetime, and offline preview) take
/// part. The resulting JSON tree is then hashed in a tagged binary form: object members sorted
/// by ordinal name, arrays in order, strings as unescaped UTF-8, and numbers as the model
/// serializer's round-trip text. The hash therefore does not depend on JSON property order or on
/// the serializer's escaping choices. Fingerprints are desktop-local and never cross the wire.
/// </remarks>
public static class CanonicalCommandFingerprint
{
    private const string Domain = "TarkovCompanion.PairedDevice/v2/command-fingerprint";

    public static CommandFingerprint Compute(CompanionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var modelJson = JsonSerializer.SerializeToUtf8Bytes(
            command,
            typeof(CompanionCommand),
            CompanionProtocolJson.Options);
        using var document = JsonDocument.Parse(
            modelJson,
            new JsonDocumentOptions { MaxDepth = ProtocolBounds.MaxJsonDepth });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBytes(hash, Encoding.UTF8.GetBytes(Domain));
        Append(hash, document.RootElement);
        return new CommandFingerprint(ProtocolGuard.EncodeBase64Url(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                AppendTag(hash, (byte)'o', properties.Length);
                foreach (var property in properties)
                {
                    AppendBytes(hash, Encoding.UTF8.GetBytes(property.Name));
                    Append(hash, property.Value);
                }

                break;
            case JsonValueKind.Array:
                AppendTag(hash, (byte)'a', element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    Append(hash, item);
                }

                break;
            case JsonValueKind.String:
                hash.AppendData([(byte)'s']);
                AppendBytes(hash, Encoding.UTF8.GetBytes(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                hash.AppendData([(byte)'n']);
                AppendBytes(hash, Encoding.UTF8.GetBytes(element.GetRawText()));
                break;
            case JsonValueKind.True:
                hash.AppendData([(byte)'t']);
                break;
            case JsonValueKind.False:
                hash.AppendData([(byte)'f']);
                break;
            case JsonValueKind.Null:
                hash.AppendData([(byte)'z']);
                break;
            default:
                throw new JsonException($"Unsupported command token {element.ValueKind}.");
        }
    }

    private static void AppendTag(IncrementalHash hash, byte tag, int count)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[1..], checked((uint)count));
        hash.AppendData(header);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
