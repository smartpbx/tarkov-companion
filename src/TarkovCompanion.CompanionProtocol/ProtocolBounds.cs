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

    /// <summary>
    /// Plaintext inside a relay frame: a two-byte payload kind followed by one JSON root of at most
    /// <see cref="MaxPayloadBytes"/>.
    /// </summary>
    public const int MaxRelayPlaintextBytes = MaxPayloadBytes + 2;

    /// <summary>The smallest relay plaintext: a payload kind and a one-byte JSON root.</summary>
    public const int MinRelayPlaintextBytes = 3;

    /// <summary>
    /// Headroom a committed state keeps below <see cref="MaxPayloadBytes"/> so server-time maintenance,
    /// which can lengthen a status or timestamp by a few bytes, never produces undeliverable state.
    /// </summary>
    public const int MaintenanceReserveBytes = 2 * 1024;

    /// <summary>2^53-1: every integer on the wire stays exactly representable as a JavaScript number.</summary>
    public const long MaxWireInteger = 9_007_199_254_740_991;

    public const int MaxDeepLinkBytes = 512;
    public const int PairingCodeCharacters = 10;
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
    public const int MaxProfilePreferenceItems = 256;
    public const int MaxProtectedItemRules = 256;
    public const int MaxRecommendationOverrides = 256;
    public const int MaxFavoriteLoadouts = 64;
    public const int MaxFavoriteLoadoutItems = 64;
    public const int MaxSharedPersonalization = 64;
    public const int MaxPreferenceQuantity = 1_000_000;
    public const int MaxPreferenceSortOrder = 1_000_000;

    public const double MaxWorldCoordinateMagnitude = 1_000_000;

    public static TimeSpan PairingLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan MaximumPairingLifetime { get; } = TimeSpan.FromMinutes(10);
    public static TimeSpan PairingRateWindow { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan HandshakeChallengeLifetime { get; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// How long one paired session may live before the device has to pair again.
    /// </summary>
    /// <remarks>
    /// [#290] Twelve hours until 2026-09-20, which with no resume path wired anywhere meant every
    /// tablet was paired from scratch (code, comparison, approval) every day it was used, and the
    /// desktop re-claimed its relay as often. A session is a per-device credential the desktop can
    /// revoke at once, so the bound is now as long as the relay keeps a device at all.
    /// </remarks>
    public static TimeSpan MaximumSessionLifetime { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The bound every build before 2026-09-20 enforces. A desktop talking to a relay that does not
    /// say it accepts more asks for this, because that relay refuses anything longer outright.
    /// </summary>
    public static TimeSpan LegacyMaximumSessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan CommandLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan OfflineQueueLifetime { get; } = TimeSpan.FromMinutes(15);
    public static TimeSpan MaxClientClockSkew { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan CaptureIntentLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan ControlLeaseLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan MaximumControlLeaseLifetime { get; } = TimeSpan.FromMinutes(5);
    public static TimeSpan PingLifetime { get; } = TimeSpan.FromSeconds(45);
    public static TimeSpan MaintenanceScanInterval { get; } = TimeSpan.FromHours(1);
    /// <summary>How long a paired device may go unused before it has to pair again.</summary>
    /// <remarks>
    /// [#290] Two hours until 2026-09-20: a tablet left on the desk overnight, or a desktop that
    /// was simply switched off, had expired by the next evening on both sides.
    /// </remarks>
    public static TimeSpan DeviceInactivityExpiry { get; } = TimeSpan.FromDays(14);
}

internal static class ProtocolGuard
{
    private static readonly System.Text.RegularExpressions.Regex DeepLinkPattern = new(
        "\\Atarkov-companion://[a-z][a-z0-9-]{0,31}(/(?!\\.{1,2}(/|\\z))[A-Za-z0-9._~-]{1,128}){1,4}\\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly System.Numerics.BigInteger P256Prime = System.Numerics.BigInteger.Parse(
        "115792089210356248762697446949407573530086143415290314195533631308867097853951",
        System.Globalization.CultureInfo.InvariantCulture);

    private static readonly System.Numerics.BigInteger P256B = System.Numerics.BigInteger.Parse(
        "41058363725152142129326129780047268409114441015993725554835256314039467401291",
        System.Globalization.CultureInfo.InvariantCulture);

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

    public static long WireInteger(long value, string parameterName) =>
        value is >= 0 and <= ProtocolBounds.MaxWireInteger
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Wire integers stay between 0 and 2^53-1.");

    public static TimeSpan LeaseDuration(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero &&
        value <= ProtocolBounds.MaximumControlLeaseLifetime &&
        value.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "A control lease is a whole number of milliseconds up to five minutes.");

    /// <summary>
    /// Accepts only <c>tarkov-companion://kind/segment[/segment...]</c> with unreserved characters and
    /// no dot segments, so a hostile tablet cannot place a shell, file, or web URI, or a traversal, in
    /// desktop focus state. The desktop routes it internally and never hands it to the OS shell.
    /// </summary>
    public static string? DeepLink(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > ProtocolBounds.MaxDeepLinkBytes || !DeepLinkPattern.IsMatch(value))
        {
            throw new ArgumentException("A deep link is tarkov-companion://kind/segment with unreserved characters.", parameterName);
        }

        return value;
    }

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
        if (!bytes.AsSpan(0, P256SubjectPublicKeyInfoPrefix.Length).SequenceEqual(P256SubjectPublicKeyInfoPrefix) ||
            !IsOnP256(bytes.AsSpan(27, 32), bytes.AsSpan(59, 32)))
        {
            throw new ArgumentException("Expected an uncompressed P-256 SubjectPublicKeyInfo whose point is on the curve.", parameterName);
        }

        return bytes;
    }

    /// <summary>True when the affine point satisfies y^2 = x^3 - 3x + b over the P-256 prime field.</summary>
    public static bool IsOnP256(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var px = new System.Numerics.BigInteger(x, isUnsigned: true, isBigEndian: true);
        var py = new System.Numerics.BigInteger(y, isUnsigned: true, isBigEndian: true);
        if (px >= P256Prime || py >= P256Prime)
        {
            return false;
        }

        var left = System.Numerics.BigInteger.ModPow(py, 2, P256Prime);
        var right = (System.Numerics.BigInteger.ModPow(px, 3, P256Prime) - (3 * px) + P256B) % P256Prime;
        if (right.Sign < 0)
        {
            right += P256Prime;
        }

        return left == right;
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
