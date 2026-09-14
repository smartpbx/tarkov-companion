using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Profile;

/// <summary>
/// A bounded, checksummed exchange format. The parser rejects unknown members and does not
/// deserialize an unreviewed document into storage; Application must first create a preview.
/// </summary>
public sealed class JsonProfileContextTransferCodec : IProfileTransferCodec
{
    public const string FormatId = "tarkov-companion.profile-context";
    public const int FormatVersion = 1;
    private const int MaximumBytes = 2_097_152;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public string Write(ProfileTransferDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.FormatVersion != FormatVersion) throw new ArgumentOutOfRangeException(nameof(document));
        var payload = CanonicalPayload(document.ExportedUtc, document.Profiles);
        var envelope = new TransferEnvelope(FormatId, FormatVersion, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(), JsonSerializer.Deserialize<TransferPayload>(payload, Options)!);
        var result = JsonSerializer.Serialize(envelope, Options);
        if (Encoding.UTF8.GetByteCount(result) > MaximumBytes) throw new InvalidOperationException("Profile export exceeds the 2 MiB format limit.");
        return result;
    }

    public ProfileTransferDocument Read(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        if (Encoding.UTF8.GetByteCount(document) > MaximumBytes) throw new InvalidDataException("Profile import exceeds the 2 MiB format limit.");
        try
        {
            var envelope = JsonSerializer.Deserialize<TransferEnvelope>(document, Options)
                ?? throw new InvalidDataException("Profile import is empty.");
            if (!string.Equals(envelope.FormatId, FormatId, StringComparison.Ordinal) || envelope.FormatVersion != FormatVersion)
                throw new InvalidDataException("Profile import has an unsupported format or version.");
            if (envelope.Payload is null || envelope.Checksum is null || envelope.Checksum.Length != 64 || envelope.Checksum.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("Profile import has a missing or malformed checksum.");
            if (envelope.Payload.Profiles is null || envelope.Payload.Profiles.Count is > 64)
                throw new InvalidDataException("Profile import has an invalid profile count.");

            _ = new ProfileWorkspaceSnapshot(0, null, envelope.Payload.Profiles);
            var payload = CanonicalPayload(envelope.Payload.ExportedUtc, envelope.Payload.Profiles);
            var expected = Convert.FromHexString(envelope.Checksum);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Profile import checksum does not match the reviewed payload.");
            return new(FormatVersion, envelope.Payload.ExportedUtc.ToUniversalTime(), Array.AsReadOnly(envelope.Payload.Profiles.ToArray()));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Profile import is not valid profile-context JSON.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("Profile import contains an unsupported value.", exception);
        }
    }

    private static string CanonicalPayload(DateTimeOffset exportedUtc, IReadOnlyList<ProfileRecord> profiles) => JsonSerializer.Serialize(
        new TransferPayload(exportedUtc.ToUniversalTime(), profiles.OrderBy(profile => profile.Context.Identity.ProfileId).ToArray()), Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record TransferEnvelope(string FormatId, int FormatVersion, string Checksum, TransferPayload Payload);
    private sealed record TransferPayload(DateTimeOffset ExportedUtc, IReadOnlyList<ProfileRecord> Profiles);
}
