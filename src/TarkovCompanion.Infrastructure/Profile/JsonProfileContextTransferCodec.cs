using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Profile;

/// <summary>
/// A bounded, checksummed exchange format. Application must still create a preview before any
/// imported profile can change state.
///
/// External JSON follows the repository rule: unknown members are tolerated, while a missing
/// required member, a null where a value is required, or an enum name this version does not know
/// fails the read. Tolerated is not compatible. The checksum is computed over this version's
/// re-serialization of the typed Core records it decoded, not over the received text, so a member
/// this version does not know is dropped before hashing. A document carrying one verifies only if
/// its writer also left that member out of the hash; a newer writer that hashed it fails here.
///
/// An earlier remark promised that an additive member from a newer writer would verify. It only
/// did in a test that added the member after the checksum was taken. Adding, renaming, or
/// reordering a member of any hashed record, or changing how a date, number, enum, or string is
/// written, changes the checksum of every existing v1 document, so it is a format change that
/// raises <see cref="FormatVersion"/>, never an additive one. The committed v1 fixture in
/// fixtures/profiles fails the build on any such change, so none can land unnoticed.
///
/// The same frozen copy is hashed, returned, and (on write) emitted, so the verified profiles are
/// the ones handed on.
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
        var frozen = new ProfileTransferDocument(document.FormatVersion, document.ExportedUtc, document.Profiles);
        var transferPayload = new TransferPayload(frozen.ExportedUtc, frozen.Profiles);
        var payload = CanonicalPayload(transferPayload);
        var envelope = new TransferEnvelope(
            FormatId,
            FormatVersion,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            transferPayload);
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
            if (envelope.Payload.Profiles is null)
                throw new InvalidDataException("Profile import has no profile list.");

            var frozen = new ProfileTransferDocument(FormatVersion, envelope.Payload.ExportedUtc, envelope.Payload.Profiles);
            var payload = CanonicalPayload(new TransferPayload(frozen.ExportedUtc, frozen.Profiles));
            var expected = Convert.FromHexString(envelope.Checksum);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Profile import checksum does not match the reviewed payload.");
            return frozen;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Profile import is not valid profile-context JSON.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("Profile import contains an unsupported value.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Profile import has an invalid profile context.", exception);
        }
    }

    private static string CanonicalPayload(TransferPayload payload) => JsonSerializer.Serialize(payload, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 64,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed record TransferEnvelope(string FormatId, int FormatVersion, string Checksum, TransferPayload Payload);
    private sealed record TransferPayload(DateTimeOffset ExportedUtc, IReadOnlyList<ProfileRecord> Profiles);
}
