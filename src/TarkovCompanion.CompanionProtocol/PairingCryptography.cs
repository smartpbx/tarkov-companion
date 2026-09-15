using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.CompanionProtocol;

public enum PairingTrafficDirection
{
    TabletToDesktop = 1,
    DesktopToTablet,
}

/// <summary>
/// Frozen protocol-2 encodings for the pairing commitment, sealed device name, verification code,
/// handshake transcript, desktop identity signature, HKDF traffic-key schedule, relay nonce, and
/// AES-GCM additional authenticated data.
/// </summary>
/// <remarks>
/// The binary encodings are deliberately independent of JSON property order. Every variable byte
/// string is prefixed by an unsigned 32-bit big-endian byte count; integers and RFC 9562 UUID
/// bytes are big-endian; instants are signed 64-bit Unix milliseconds. Base64url fields are
/// decoded to their exact bytes first. Two gateway implementations therefore cannot sign or
/// authenticate subtly different transcripts. docs/PAIRED_DEVICE_PROTOCOL.md lists every field in
/// order, and the test vectors under Golden/crypto were computed by an independent implementation.
/// </remarks>
public static class PairingCryptography
{
    public const string PairingRequestContextDomain = "TarkovCompanion.PairedDevice/v2/pairing-request-context";
    public const string DeviceNameKeyLabel = "TarkovCompanion.PairedDevice/v2/device-name-key";
    public const string PairingCommitmentDomain = "TarkovCompanion.PairedDevice/v2/pairing-commitment";
    public const string TranscriptDomain = "TarkovCompanion.PairedDevice/v2/handshake-transcript";
    public const string DesktopSignatureDomain = "TarkovCompanion.PairedDevice/v2/desktop-handshake-signature";
    public const string HkdfTabletToDesktopLabel = "TarkovCompanion.PairedDevice/v2/tablet-to-desktop";
    public const string HkdfDesktopToTabletLabel = "TarkovCompanion.PairedDevice/v2/desktop-to-tablet";
    public const string RelayAadDomain = "TarkovCompanion.PairedDevice/v2/relay-aad";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] EncodePairingRequestContext(
        PairingOffer offer,
        CompanionProtocolVersion negotiatedVersion,
        DevicePublicKey deviceKey,
        EphemeralPublicKey ephemeralKey,
        string clientNonceBase64Url)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(deviceKey);
        ArgumentNullException.ThrowIfNull(ephemeralKey);
        var writer = new ProtocolBinaryWriter();
        writer.Utf8(PairingRequestContextDomain);
        writer.Version(ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion)));
        writer.Uuid(offer.AttemptId.Value);
        writer.Base64Url(offer.DesktopIdentityKey.KeyId.Value);
        writer.Base64Url(offer.DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url);
        writer.Base64Url(offer.DesktopNonceBase64Url);
        writer.Base64Url(deviceKey.KeyId.Value);
        writer.Base64Url(deviceKey.CredentialIdBase64Url);
        writer.Base64Url(ephemeralKey.SubjectPublicKeyInfoBase64Url);
        writer.Base64Url(ProtocolGuard.Base64Url(
            clientNonceBase64Url,
            nameof(clientNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes));
        writer.Instant(offer.ExpiresUtc);
        return writer.ToArray();
    }

    public static byte[] ComputePairingRequestContextHash(
        PairingOffer offer,
        CompanionProtocolVersion negotiatedVersion,
        DevicePublicKey deviceKey,
        EphemeralPublicKey ephemeralKey,
        string clientNonceBase64Url) =>
        SHA256.HashData(EncodePairingRequestContext(offer, negotiatedVersion, deviceKey, ephemeralKey, clientNonceBase64Url));

    /// <summary>
    /// Seals the requested display name with AES-256-GCM. The key is HKDF-SHA-256 over the tablet
    /// ephemeral/desktop-offer ECDH secret, salted by the request-context hash; the nonce is 12 zero
    /// bytes because that key is used exactly once; the context hash is the additional data.
    /// </summary>
    public static SealedDeviceName SealDeviceName(
        ReadOnlySpan<byte> p256SharedSecret,
        ReadOnlySpan<byte> pairingRequestContextHash,
        string deviceName)
    {
        var plaintext = Encoding.UTF8.GetBytes(ValidateDeviceName(deviceName));
        var key = DeriveDeviceNameKey(p256SharedSecret, pairingRequestContextHash);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ProtocolBounds.RelayAuthenticationTagBytes];
        using (var aes = new AesGcm(key, ProtocolBounds.RelayAuthenticationTagBytes))
        {
            aes.Encrypt(new byte[ProtocolBounds.RelayNonceBytes], plaintext, ciphertext, tag, pairingRequestContextHash);
        }

        CryptographicOperations.ZeroMemory(key);
        return new SealedDeviceName(ProtocolGuard.EncodeBase64Url(ciphertext), ProtocolGuard.EncodeBase64Url(tag));
    }

    /// <summary>Opens a sealed name for the desktop approval prompt; tampering throws <see cref="CryptographicException"/>.</summary>
    public static string OpenDeviceName(ReadOnlySpan<byte> p256SharedSecret, PairingOffer offer, PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = ComputePairingRequestContextHash(
            offer,
            request.NegotiatedVersion,
            request.DeviceKey,
            request.EphemeralKey,
            request.ClientNonceBase64Url);
        var ciphertext = ProtocolGuard.DecodeBase64Url(
            request.RequestedDeviceName.CiphertextBase64Url,
            nameof(request),
            maximumDecodedBytes: ProtocolBounds.MaxDeviceNameBytes);
        var tag = ProtocolGuard.DecodeBase64Url(
            request.RequestedDeviceName.AuthenticationTagBase64Url,
            nameof(request),
            exactDecodedBytes: ProtocolBounds.RelayAuthenticationTagBytes);
        var key = DeriveDeviceNameKey(p256SharedSecret, context);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, ProtocolBounds.RelayAuthenticationTagBytes);
            aes.Decrypt(new byte[ProtocolBounds.RelayNonceBytes], ciphertext, tag, plaintext, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        string name;
        try
        {
            name = StrictUtf8.GetString(plaintext);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException("The sealed device name is not valid UTF-8.", exception);
        }

        return ValidateDeviceName(name);
    }

    /// <summary>SHA-256 over the request context hash and the exact sealed name.</summary>
    public static byte[] ComputePairingCommitment(PairingOffer offer, PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (offer.AttemptId != request.AttemptId)
        {
            throw new ArgumentException("The pairing request answers another offer.", nameof(request));
        }

        var writer = new ProtocolBinaryWriter();
        writer.Utf8(PairingCommitmentDomain);
        writer.Bytes(ComputePairingRequestContextHash(
            offer,
            request.NegotiatedVersion,
            request.DeviceKey,
            request.EphemeralKey,
            request.ClientNonceBase64Url));
        writer.Base64Url(request.RequestedDeviceName.CiphertextBase64Url);
        writer.Base64Url(request.RequestedDeviceName.AuthenticationTagBase64Url);
        return SHA256.HashData(writer.ToArray());
    }

    /// <summary>
    /// The six-digit code both screens show before desktop approval: the first four commitment
    /// bytes as an unsigned big-endian integer, modulo one million, zero padded. A relay that
    /// substitutes either ephemeral key, nonce, device key, or name changes the code.
    /// </summary>
    public static string ComputeVerificationCode(PairingOffer offer, PairingRequest request)
    {
        var commitment = ComputePairingCommitment(offer, request);
        var value = BinaryPrimitives.ReadUInt32BigEndian(commitment) % 1_000_000u;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static byte[] EncodeDesktopSignatureInput(ReadOnlySpan<byte> transcriptHash)
    {
        if (transcriptHash.Length != ProtocolBounds.TranscriptHashBytes)
        {
            throw new ArgumentException("A transcript hash is exactly 32 bytes.", nameof(transcriptHash));
        }

        var writer = new ProtocolBinaryWriter();
        writer.Utf8(DesktopSignatureDomain);
        writer.Bytes(transcriptHash);
        return writer.ToArray();
    }

    public static bool VerifyDesktopSignature(
        DesktopIdentityKey desktopIdentityKey,
        ReadOnlySpan<byte> transcriptHash,
        ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(desktopIdentityKey);
        if (transcriptHash.Length != ProtocolBounds.TranscriptHashBytes ||
            signature.Length != ProtocolBounds.DesktopSignatureBytes)
        {
            return false;
        }

        var spki = ProtocolGuard.P256SubjectPublicKeyInfo(
            desktopIdentityKey.SubjectPublicKeyInfoBase64Url,
            nameof(desktopIdentityKey));
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.KeySize == 256 &&
                   ecdsa.VerifyData(
                       EncodeDesktopSignatureInput(transcriptHash),
                       signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The raw 32-byte P-256 ECDH secret (the shared point's x coordinate).</summary>
    public static byte[] DeriveP256SharedSecret(ECDiffieHellman localKey, EphemeralPublicKey remoteKey)
    {
        ArgumentNullException.ThrowIfNull(localKey);
        ArgumentNullException.ThrowIfNull(remoteKey);
        if (localKey.KeySize != 256)
        {
            throw new ArgumentException("The local ephemeral key is a P-256 key.", nameof(localKey));
        }

        var spki = ProtocolGuard.P256SubjectPublicKeyInfo(remoteKey.SubjectPublicKeyInfoBase64Url, nameof(remoteKey));
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(spki, out _);
        using var remotePublicKey = remote.PublicKey;
        return localKey.DeriveRawSecretAgreement(remotePublicKey);
    }

    /// <summary>
    /// Derives one directional AES-256-GCM key: HKDF-SHA-256 with the ECDH secret as input keying
    /// material, the 32-byte transcript hash as salt, and the exact UTF-8 direction label as info.
    /// </summary>
    public static byte[] DeriveTrafficKey(
        ReadOnlySpan<byte> p256SharedSecret,
        string transcriptHashBase64Url,
        PairingTrafficDirection direction)
    {
        RequireSharedSecret(p256SharedSecret);
        ProtocolGuard.Defined(direction, nameof(direction));
        var salt = ProtocolGuard.DecodeBase64Url(
            transcriptHashBase64Url,
            nameof(transcriptHashBase64Url),
            exactDecodedBytes: ProtocolBounds.TranscriptHashBytes);
        var label = direction == PairingTrafficDirection.TabletToDesktop
            ? HkdfTabletToDesktopLabel
            : HkdfDesktopToTabletLabel;
        var output = new byte[ProtocolBounds.TrafficKeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, p256SharedSecret, output, salt, Encoding.UTF8.GetBytes(label));
        return output;
    }

    /// <summary>Encodes the 32-bit key epoch and 64-bit sender sequence, both big-endian.</summary>
    public static byte[] EncodeRelayNonce(long keyEpoch, long senderSequence)
    {
        var epoch = ProtocolGuard.KeyEpoch(keyEpoch, nameof(keyEpoch));
        var sequence = RequireSenderSequence(senderSequence);
        var nonce = new byte[ProtocolBounds.RelayNonceBytes];
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(0, 4), (uint)epoch);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), (ulong)sequence);
        return nonce;
    }

    public static byte[] EncodeRelayAdditionalAuthenticatedData(
        PairingTrafficDirection direction,
        CompanionProtocolVersion protocolVersion,
        RelayChannelId channelId,
        DeviceSessionId sessionId,
        long keyEpoch,
        long senderSequence,
        RelayCipherSuite cipherSuite,
        int ciphertextLength,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ProtocolGuard.Defined(direction, nameof(direction));
        ProtocolGuard.Defined(cipherSuite, nameof(cipherSuite));
        if (ciphertextLength is <= 0 or > ProtocolBounds.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ciphertextLength));
        }

        var writer = new ProtocolBinaryWriter();
        writer.Utf8(RelayAadDomain);
        writer.UInt16((ushort)direction);
        writer.Version(ProtocolGuard.Version(protocolVersion, nameof(protocolVersion)));
        writer.Uuid(ProtocolGuard.Id(channelId.Value, nameof(channelId)));
        writer.Uuid(ProtocolGuard.Id(sessionId.Value, nameof(sessionId)));
        writer.UInt32((uint)ProtocolGuard.KeyEpoch(keyEpoch, nameof(keyEpoch)));
        writer.UInt64((ulong)RequireSenderSequence(senderSequence));
        writer.UInt16((ushort)cipherSuite);
        writer.UInt32((uint)ciphertextLength);
        writer.Instant(ProtocolGuard.Utc(issuedUtc, nameof(issuedUtc)));
        writer.Instant(ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc)));
        return writer.ToArray();
    }

    /// <summary>
    /// Encrypts one plaintext protocol root into an opaque relay frame using the sender's
    /// directional key. The sender must never reuse a (key epoch, sender sequence) pair.
    /// </summary>
    public static OpaqueRelayFrame SealRelayFrame(
        ReadOnlySpan<byte> trafficKey,
        PairingTrafficDirection direction,
        CompanionProtocolVersion protocolVersion,
        RelayChannelId channelId,
        DeviceSessionId sessionId,
        long keyEpoch,
        long senderSequence,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        ReadOnlySpan<byte> plaintext)
    {
        RequireTrafficKey(trafficKey);
        if (plaintext.Length is 0 or > ProtocolBounds.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "A relay plaintext is 1-65536 bytes.");
        }

        const RelayCipherSuite suite = RelayCipherSuite.P256HkdfSha256Aes256Gcm;
        var nonce = EncodeRelayNonce(keyEpoch, senderSequence);
        var aad = EncodeRelayAdditionalAuthenticatedData(
            direction,
            protocolVersion,
            channelId,
            sessionId,
            keyEpoch,
            senderSequence,
            suite,
            plaintext.Length,
            issuedUtc,
            expiresUtc);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ProtocolBounds.RelayAuthenticationTagBytes];
        using (var aes = new AesGcm(trafficKey, ProtocolBounds.RelayAuthenticationTagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        var text = ProtocolGuard.EncodeBase64Url(ciphertext);
        var chunks = new List<string>((text.Length / ProtocolBounds.RelayCiphertextChunkCharacters) + 1);
        for (var offset = 0; offset < text.Length; offset += ProtocolBounds.RelayCiphertextChunkCharacters)
        {
            chunks.Add(text.Substring(offset, Math.Min(ProtocolBounds.RelayCiphertextChunkCharacters, text.Length - offset)));
        }

        return new OpaqueRelayFrame(
            protocolVersion,
            channelId,
            sessionId,
            keyEpoch,
            senderSequence,
            suite,
            ProtocolGuard.EncodeBase64Url(nonce),
            ciphertext.Length,
            chunks,
            ProtocolGuard.EncodeBase64Url(tag),
            issuedUtc,
            expiresUtc);
    }

    /// <summary>
    /// Authenticates and decrypts a relay frame with the receiver's expected direction. Any changed
    /// routing field, direction, ciphertext byte, or tag throws <see cref="CryptographicException"/>.
    /// </summary>
    public static byte[] OpenRelayFrame(
        ReadOnlySpan<byte> trafficKey,
        PairingTrafficDirection direction,
        OpaqueRelayFrame frame)
    {
        RequireTrafficKey(trafficKey);
        ArgumentNullException.ThrowIfNull(frame);
        var ciphertext = frame.DecodeCiphertext();
        var tag = ProtocolGuard.DecodeBase64Url(
            frame.AuthenticationTagBase64Url,
            nameof(frame),
            exactDecodedBytes: ProtocolBounds.RelayAuthenticationTagBytes);
        var nonce = EncodeRelayNonce(frame.KeyEpoch, frame.SenderSequence);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(trafficKey, ProtocolBounds.RelayAuthenticationTagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, frame.EncodeAdditionalAuthenticatedData(direction));
        return plaintext;
    }

    internal static string ValidateDeviceName(string? deviceName)
    {
        var name = ProtocolGuard.Required(deviceName, nameof(deviceName), ProtocolBounds.MaxDeviceNameBytes);
        foreach (var character in name)
        {
            // Control and bidirectional-override characters could make the approval prompt show a
            // different name from the one the tablet requested.
            if (char.IsControl(character) ||
                character is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            {
                throw new ArgumentException("A device name cannot contain control or bidirectional-override characters.", nameof(deviceName));
            }
        }

        return name;
    }

    private static byte[] DeriveDeviceNameKey(ReadOnlySpan<byte> p256SharedSecret, ReadOnlySpan<byte> contextHash)
    {
        RequireSharedSecret(p256SharedSecret);
        if (contextHash.Length != ProtocolBounds.TranscriptHashBytes)
        {
            throw new ArgumentException("A pairing request context hash is exactly 32 bytes.", nameof(contextHash));
        }

        var key = new byte[ProtocolBounds.TrafficKeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, p256SharedSecret, key, contextHash, Encoding.UTF8.GetBytes(DeviceNameKeyLabel));
        return key;
    }

    private static void RequireSharedSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != ProtocolBounds.P256SharedSecretBytes)
        {
            throw new ArgumentException("A P-256 ECDH shared secret is exactly 32 bytes.", nameof(secret));
        }
    }

    private static void RequireTrafficKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != ProtocolBounds.TrafficKeyBytes)
        {
            throw new ArgumentException("A traffic key is exactly 32 bytes.", nameof(key));
        }
    }

    private static long RequireSenderSequence(long senderSequence) =>
        senderSequence is > 0 and <= ProtocolBounds.MaxSenderSequence
            ? senderSequence
            : throw new ArgumentOutOfRangeException(
                nameof(senderSequence),
                "A sender sequence is 1 through 2^32-1; the session must re-key before it would repeat.");
}

/// <summary>Length-prefixed big-endian writer for the frozen binary encodings.</summary>
internal sealed class ProtocolBinaryWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public void Utf8(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    public void Base64Url(string value) => Bytes(ProtocolGuard.DecodeBase64Url(value, nameof(value)));

    public void Bytes(ReadOnlySpan<byte> value)
    {
        var target = _buffer.GetSpan(checked(4 + value.Length));
        BinaryPrimitives.WriteUInt32BigEndian(target, checked((uint)value.Length));
        value.CopyTo(target[4..]);
        _buffer.Advance(4 + value.Length);
    }

    public void Uuid(Guid value)
    {
        var target = _buffer.GetSpan(16);
        if (!value.TryWriteBytes(target, bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode an RFC 9562 UUID.");
        }

        _buffer.Advance(16);
    }

    public void Version(CompanionProtocolVersion version)
    {
        UInt16(checked((ushort)version.Major));
        UInt16(checked((ushort)version.Minor));
    }

    public void Instant(DateTimeOffset value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_buffer.GetSpan(8), value.ToUnixTimeMilliseconds());
        _buffer.Advance(8);
    }

    public void UInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
    }

    public void UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    public void UInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}
