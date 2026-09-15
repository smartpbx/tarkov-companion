using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The resource and lifetime limits shared by every paired-device transport.</summary>
/// <remarks>
/// These are protocol limits rather than implementation suggestions. Keeping them here prevents
/// a direct-LAN adapter and the relay adapter from accepting different messages.
/// </remarks>
public static class ProtocolBounds
{
    /// <summary>Every plaintext JSON root except the relay frame, including a full canonical snapshot.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>
    /// The relay frame JSON carries base64url text for up to <see cref="MaxPayloadBytes"/> of
    /// ciphertext, so its own lexical bound is larger than the plaintext it protects.
    /// </summary>
    public const int MaxRelayFrameBytes = 96 * 1024;

    public const int MaxStringBytes = 1024;
    public const int MaxShortStringBytes = 128;
    public const int MaxCollectionItems = 256;
    public const int MaxJsonDepth = 16;
    public const int MaxDevices = 32;
    public const int MaxMarks = 256;
    public const int MaxRecentCommands = 256;
    public const int MaxOfflineQueueItems = 64;
    public const int MaxDeliveryItemsPerChannel = 64;
    public const int MaxReplayItems = 256;
    public const int MaxPairingAttemptsPerWindow = 5;
    public const int PairingNonceBytes = 32;
    public const int TranscriptHashBytes = 32;
    public const int TrafficKeyBytes = 32;
    public const int P256SharedSecretBytes = 32;
    public const int P256SubjectPublicKeyInfoBytes = 91;
    public const int DesktopSignatureBytes = 64;
    public const int RelayNonceBytes = 12;
    public const int RelayAuthenticationTagBytes = 16;
    public const int RelayCiphertextChunkCharacters = 1024;
    public const long MaxKeyEpoch = uint.MaxValue;
    public const long MaxSenderSequence = uint.MaxValue;
    public const int MaxCredentialIdBytes = 512;
    public const int MaxCosePublicKeyBytes = 256;
    public const int MinAuthenticatorDataBytes = 37;
    public const int MaxAuthenticatorDataBytes = 512;
    public const int MaxClientDataJsonBytes = 512;
    public const int MaxAssertionSignatureBytes = 80;
    public const int MaxDeviceNameBytes = 128;
    public const int VerificationCodeDigits = 6;
    public const int MaxCaptureProvenanceDepth = 3;

    public const double MaxWorldCoordinateMagnitude = 1_000_000;

    public static TimeSpan PairingLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan MaximumPairingLifetime { get; } = TimeSpan.FromMinutes(10);
    public static TimeSpan PairingRateWindow { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan HandshakeChallengeLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan MaximumSessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan CommandLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan OfflineQueueLifetime { get; } = TimeSpan.FromMinutes(15);
    public static TimeSpan MaxClientClockSkew { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan CaptureIntentLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan ControlLeaseLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan MaximumControlLeaseLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan PingLifetime { get; } = TimeSpan.FromSeconds(45);
    public static TimeSpan MaintenanceScanInterval { get; } = TimeSpan.FromHours(1);
    public static TimeSpan DeviceInactivityExpiry { get; } = TimeSpan.FromHours(2);
}

internal static class ProtocolGuard
{
    // DER SubjectPublicKeyInfo header for id-ecPublicKey with prime256v1, followed by an
    // uncompressed point marker. WebCrypto exportKey("spki") and .NET ExportSubjectPublicKeyInfo
    // both produce exactly this prefix for ECDH and ECDSA P-256 keys.
    private static readonly byte[] P256SubjectPublicKeyInfoPrefix =
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];

    public static string Required(string? value, string parameterName, int maxBytes = ProtocolBounds.MaxStringBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (Encoding.UTF8.GetByteCount(trimmed) > maxBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"UTF-8 text exceeds {maxBytes} bytes.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, string parameterName, int maxBytes = ProtocolBounds.MaxStringBytes) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, parameterName, maxBytes);

    public static ReadOnlyCollection<T> List<T>(
        IEnumerable<T>? values,
        string parameterName,
        int maximum = ProtocolBounds.MaxCollectionItems)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = values.ToArray();
        if (copy.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"A collection may contain at most {maximum} items.");
        }

        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Protocol collections cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A timestamp with an explicit UTC zero offset is required.", parameterName);
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Protocol timestamps have millisecond precision.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset? UtcOptional(DateTimeOffset? value, string parameterName) =>
        value is null ? null : Utc(value.Value, parameterName);

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) && Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, $"Undefined {typeof(TEnum).Name} value.");

    public static T NotNull<T>(T? value, string parameterName)
        where T : class => value ?? throw new ArgumentNullException(parameterName);

    public static Guid Id(Guid value, string parameterName) =>
        value != Guid.Empty ? value : throw new ArgumentException("An identifier is required.", parameterName);

    public static long NonNegative(long value, string parameterName) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    public static long Positive(long value, string parameterName) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

    public static CompanionProtocolVersion Version(CompanionProtocolVersion value, string parameterName) =>
        value.IsDefined ? value : throw new ArgumentException("A protocol version is required.", parameterName);

    public static long KeyEpoch(long value, string parameterName) =>
        value is > 0 and <= ProtocolBounds.MaxKeyEpoch
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "A key epoch is an unsigned 32-bit value above zero.");

    public static string Base64Url(
        string? value,
        string parameterName,
        int maximumCharacters = ProtocolBounds.MaxStringBytes,
        int? exactDecodedBytes = null,
        int? maximumDecodedBytes = null)
    {
        _ = DecodeCanonicalBase64Url(value, parameterName, maximumCharacters, exactDecodedBytes, maximumDecodedBytes);
        return value!;
    }

    public static byte[] DecodeBase64Url(
        string? value,
        string parameterName,
        int maximumCharacters = ProtocolBounds.MaxStringBytes,
        int? exactDecodedBytes = null,
        int? maximumDecodedBytes = null) =>
        DecodeCanonicalBase64Url(value, parameterName, maximumCharacters, exactDecodedBytes, maximumDecodedBytes);

    public static string EncodeBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Validates a P-256 uncompressed SubjectPublicKeyInfo and returns its exact bytes.</summary>
    public static byte[] P256SubjectPublicKeyInfo(string? value, string parameterName)
    {
        var bytes = DecodeBase64Url(
            value,
            parameterName,
            exactDecodedBytes: ProtocolBounds.P256SubjectPublicKeyInfoBytes);
        if (!bytes.AsSpan(0, P256SubjectPublicKeyInfoPrefix.Length).SequenceEqual(P256SubjectPublicKeyInfoPrefix))
        {
            throw new ArgumentException("Expected an uncompressed P-256 SubjectPublicKeyInfo.", parameterName);
        }

        return bytes;
    }

    public static string Thumbprint(ReadOnlySpan<byte> publicKeyBytes) =>
        EncodeBase64Url(SHA256.HashData(publicKeyBytes));

    /// <summary>The RFC 9562 version nibble of a UUID in its big-endian wire layout.</summary>
    public static int UuidVersion(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[6] >> 4;
    }

    public static Guid UuidVersion8(ReadOnlySpan<byte> hash)
    {
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    private static byte[] DecodeCanonicalBase64Url(
        string? value,
        string parameterName,
        int maximumCharacters,
        int? exactDecodedBytes,
        int? maximumDecodedBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value.Length > maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Base64url text exceeds {maximumCharacters} characters.");
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                throw new ArgumentException("Expected unpadded base64url text.", parameterName);
            }
        }

        var remainder = value.Length % 4;
        if (remainder == 1)
        {
            throw new ArgumentException("Expected well-formed unpadded base64url text.", parameterName);
        }

        byte[] decoded;
        try
        {
            var padding = remainder == 0 ? string.Empty : new string('=', 4 - remainder);
            decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Expected well-formed unpadded base64url text.", parameterName, exception);
        }

        // Unused trailing bits must be zero, so one byte string has exactly one wire spelling and
        // a hash, signature, or transcript input cannot be re-encoded into a second accepted form.
        if (!string.Equals(EncodeBase64Url(decoded), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected canonical base64url text.", parameterName);
        }

        if (decoded.Length == 0 ||
            (exactDecodedBytes is { } exact && decoded.Length != exact) ||
            (maximumDecodedBytes is { } maximum && decoded.Length > maximum))
        {
            throw new ArgumentException("The decoded byte length is outside the protocol bound.", parameterName);
        }

        return decoded;
    }
}
