using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// The normative binding every paired transport shares: the desktop LAN gateway, the hosted relay,
/// and the tablet. It fixes which roots may cross a connection as plaintext JSON, the one-time
/// pairing code's format and header, the QR payload, the rate limiter's source hash, and the
/// endpoint paths. See "Transport binding" in docs/PAIRED_DEVICE_PROTOCOL.md.
/// </summary>
/// <remarks>
/// Once a session is established, both the direct LAN gateway and the relay carry only
/// <see cref="OpaqueRelayFrame"/>: a session is authenticated by its traffic keys, never by a
/// session ID a peer presents, so a plaintext command, acknowledgement, update, or reconnect
/// message is refused on every transport. TLS is an outer layer only.
/// </remarks>
public static class PairedTransportBinding
{
    /// <summary>The redacted request header that carries the one-time pairing code to the offer endpoint.</summary>
    public const string PairingCodeHeaderName = "Tarkov-Pairing-Code";

    /// <summary>Crockford base32 without check symbol: 10 symbols give 50 bits for a five-minute, five-try lookup.</summary>
    public const string PairingCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int PairingCodeCharacters = ProtocolBounds.PairingCodeCharacters;

    /// <summary>POST with the pairing-code header; answers <see cref="PairingOffer"/>.</summary>
    public const string PairingOfferPath = "/v2/companion/pairing/offer";

    /// <summary>
    /// The WebSocket path. Text messages carry exactly one UTF-8 JSON root: hello and handshake roots
    /// in their documented order, then only <see cref="OpaqueRelayFrame"/> for the established session.
    /// </summary>
    public const string ConnectionPath = "/v2/companion/connect";

    public const string QrPayloadPrefix = "TARKOV-COMPANION-PAIR/2.0/";

    public const string SourceHashLabel = "TarkovCompanion.PairedDevice/v2/pairing-source";

    /// <summary>The desktop's per-start random key for source hashes is at least this long.</summary>
    public const int MinimumSourceHashKeyBytes = 32;

    private static readonly FrozenDictionary<Type, RelayPayloadKind> FramedRootKinds = new Dictionary<Type, RelayPayloadKind>
    {
        [typeof(ClientCommandEnvelope)] = RelayPayloadKind.ClientCommandEnvelope,
        [typeof(ClientDeliveryAcknowledgement)] = RelayPayloadKind.ClientDeliveryAcknowledgement,
        [typeof(ReconnectRequest)] = RelayPayloadKind.ReconnectRequest,
        [typeof(ServerEnvelope)] = RelayPayloadKind.ServerEnvelope,
        [typeof(ReconnectPlan)] = RelayPayloadKind.ReconnectPlan,
    }.ToFrozenDictionary();

    /// <summary>The roots that may cross any transport as plaintext JSON: hello, handshake, and the frame itself.</summary>
    public static IReadOnlyList<Type> PlaintextRoots { get; } =
    [
        typeof(ClientHello),
        typeof(ServerHello),
        typeof(PairingOffer),
        typeof(PairingNonceReveal),
        typeof(PairingRequest),
        typeof(SessionResumeRequest),
        typeof(HandshakeChallenge),
        typeof(DeviceKeyProof),
        typeof(SessionEstablished),
        typeof(OpaqueRelayFrame),
    ];

    /// <summary>The roots that travel only inside an authenticated frame, and the payload kind that names each.</summary>
    public static IReadOnlyDictionary<Type, RelayPayloadKind> FramedRoots => FramedRootKinds;

    /// <summary>The payload kind of a framed root; plaintext roots have none.</summary>
    public static RelayPayloadKind KindOf(Type root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return FramedRootKinds.TryGetValue(root, out var kind)
            ? kind
            : throw new ArgumentException($"{root.Name} is not carried inside a relay frame.", nameof(root));
    }

    /// <summary>A fresh one-time pairing code from the operating system CSPRNG.</summary>
    public static string GeneratePairingCode() =>
        RandomNumberGenerator.GetString(PairingCodeAlphabet, PairingCodeCharacters);

    /// <summary>
    /// Normalizes a typed or scanned code: ignores spaces and hyphens, folds case, and reads the
    /// Crockford look-alikes O as 0 and I or L as 1. Anything else that is not exactly ten alphabet
    /// symbols is refused before it reaches the rate-limited lookup.
    /// </summary>
    public static string NormalizePairingCode(string? typed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typed);
        if (typed.Length > 4 * PairingCodeCharacters)
        {
            throw new ArgumentException("A pairing code is ten symbols.", nameof(typed));
        }

        var builder = new StringBuilder(PairingCodeCharacters);
        foreach (var raw in typed)
        {
            if (raw is ' ' or '-')
            {
                continue;
            }

            var symbol = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!PairingCodeAlphabet.Contains(symbol, StringComparison.Ordinal))
            {
                throw new ArgumentException("A pairing code uses Crockford base32 symbols.", nameof(typed));
            }

            builder.Append(symbol);
        }

        return builder.Length == PairingCodeCharacters
            ? builder.ToString()
            : throw new ArgumentException("A pairing code is ten symbols.", nameof(typed));
    }

    /// <summary>
    /// The QR payload an in-app scanner reads: the prefix, the code, and the desktop identity key ID,
    /// separated by '/'. It is not a navigable URL, so the code never enters browser history.
    /// </summary>
    public static string FormatQrPayload(string pairingCode, DeviceKeyId desktopIdentityKeyId) =>
        QrPayloadPrefix + NormalizePairingCode(pairingCode) + "/" + new DeviceKeyId(desktopIdentityKeyId.Value).Value;

    public static bool TryParseQrPayload(string? payload, out string pairingCode, out DeviceKeyId desktopIdentityKeyId)
    {
        pairingCode = string.Empty;
        desktopIdentityKeyId = default;
        if (payload is null || payload.Length > 128 || !payload.StartsWith(QrPayloadPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = payload[QrPayloadPrefix.Length..].Split('/');
        if (parts.Length != 2 || parts[0].Length != PairingCodeCharacters)
        {
            return false;
        }

        try
        {
            var code = NormalizePairingCode(parts[0]);
            if (!string.Equals(code, parts[0], StringComparison.Ordinal))
            {
                return false;
            }

            desktopIdentityKeyId = new DeviceKeyId(parts[1]);
            pairingCode = code;
            return true;
        }
        catch (ArgumentException)
        {
            desktopIdentityKeyId = default;
            return false;
        }
    }

    /// <summary>
    /// The rate limiter's source hash: base64url(HMAC-SHA-256(key, text(label) ‖ bytes(source))),
    /// where the source is the IPv4 address, or the /64 prefix of an IPv6 address so one host cannot
    /// rotate through its own prefix. The key is random per desktop start and never leaves it.
    /// </summary>
    public static string ComputeSourceHash(ReadOnlySpan<byte> sourceHashKey, IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (sourceHashKey.Length < MinimumSourceHashKeyBytes)
        {
            throw new ArgumentException("A source hash key is at least 32 random bytes.", nameof(sourceHashKey));
        }

        var address = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        var bytes = address.GetAddressBytes();
        var source = address.AddressFamily == AddressFamily.InterNetworkV6 ? bytes.AsSpan(0, 8) : bytes.AsSpan();
        var writer = new ProtocolBinaryWriter();
        writer.Utf8(SourceHashLabel);
        writer.Bytes(source);
        return ProtocolGuard.EncodeBase64Url(HMACSHA256.HashData(sourceHashKey, writer.ToArray()));
    }
}
