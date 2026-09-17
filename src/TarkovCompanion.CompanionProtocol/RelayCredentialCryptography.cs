using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// The tablet's own relay bearer secret, sealed for its one-time trip through the pairing mailbox.
/// </summary>
public sealed record SealedRelayCredential(
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Seals the tablet's relay bearer secret for its trip through <c>CompanionPairingMailbox</c>
/// (v2r-tablet-marks-sync, ABUSE-PAIRED-LIVE-BEARER-THEFT). The desktop only learns this secret
/// from <c>POST /v2/companion/relay/devices</c>'s response — after <c>established</c> has already
/// gone through the mailbox once — so it travels the same bounded, attempt-scoped, single-read way
/// a second time. Left in the clear, anyone who saw the pairing code and resolved the offer could
/// read it during the mailbox's window and act as the tablet on the relay.
/// </summary>
/// <remarks>
/// Not part of the documented PAIRED_DEVICE_PROTOCOL.md relay-frame contract: it reuses
/// <see cref="PairingCryptography"/>'s AES-256-GCM primitive and one of its already-established
/// direction-specific traffic keys (desktop → tablet, since only the desktop ever seals this), but
/// with its own domain-separated additional authenticated data. The nonce is fresh and random per
/// call rather than fixed: <see cref="CompanionPairingMailbox.SubmitRelaySession"/> is first-write-wins,
/// but this method has no way to know that — a caller that retried registration (a network retry,
/// a re-registration after revoke/replace) would otherwise seal a second, different secret under
/// the same key and a fixed nonce, which is AES-GCM nonce reuse: it leaks the two plaintexts' XOR
/// and the authenticator's GHASH key, enabling forgeries. A captured ciphertext can still never be
/// replayed into <c>POST /v2/companion/relay/frames</c> and opened as an <c>OpaqueRelayFrame</c>
/// there — the JSON shape has no channel/session/keyEpoch/senderSequence at all, and even format-
/// coerced into one, this AAD's domain differs from <see cref="PairingCryptography"/>'s relay-frame
/// AAD, so authentication would fail regardless. The AAD also binds the ciphertext to the exact
/// pairing attempt and expiry it was sealed for, so it cannot be replayed against a different
/// attempt or have its expiry silently extended.
/// </remarks>
public static class RelayCredentialCryptography
{
    public const string CredentialAadDomain = "TarkovCompanion.PairedDevice/v2/relay-credential";
    private const int TagBytes = 16;
    private const int NonceBytes = 12;

    public static SealedRelayCredential Seal(
        ReadOnlySpan<byte> trafficKey,
        Guid attemptId,
        string credential,
        DateTimeOffset expiresUtc)
    {
        RequireTrafficKey(trafficKey);
        ArgumentException.ThrowIfNullOrEmpty(credential);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintext = Encoding.UTF8.GetBytes(credential);
        var aad = EncodeAdditionalAuthenticatedData(attemptId, expiresUtc);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(trafficKey, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        return new SealedRelayCredential(
            ProtocolGuard.EncodeBase64Url(nonce),
            ProtocolGuard.EncodeBase64Url(ciphertext),
            ProtocolGuard.EncodeBase64Url(tag),
            expiresUtc);
    }

    /// <summary>Opens a sealed credential; any authentication failure throws <see cref="CryptographicException"/>.</summary>
    public static string Open(ReadOnlySpan<byte> trafficKey, Guid attemptId, SealedRelayCredential sealedCredential)
    {
        RequireTrafficKey(trafficKey);
        ArgumentNullException.ThrowIfNull(sealedCredential);
        var nonce = ProtocolGuard.DecodeBase64Url(
            sealedCredential.NonceBase64Url,
            nameof(sealedCredential.NonceBase64Url),
            exactDecodedBytes: NonceBytes);
        var ciphertext = ProtocolGuard.DecodeBase64Url(
            sealedCredential.CiphertextBase64Url,
            nameof(sealedCredential.CiphertextBase64Url));
        var tag = ProtocolGuard.DecodeBase64Url(
            sealedCredential.AuthenticationTagBase64Url,
            nameof(sealedCredential.AuthenticationTagBase64Url),
            exactDecodedBytes: TagBytes);
        var aad = EncodeAdditionalAuthenticatedData(attemptId, sealedCredential.ExpiresUtc);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(trafficKey, TagBytes))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] EncodeAdditionalAuthenticatedData(Guid attemptId, DateTimeOffset expiresUtc)
    {
        var writer = new ProtocolBinaryWriter();
        writer.Utf8(CredentialAadDomain);
        writer.Uuid(attemptId);
        writer.Instant(expiresUtc);
        return writer.ToArray();
    }

    private static void RequireTrafficKey(ReadOnlySpan<byte> trafficKey)
    {
        if (trafficKey.Length != ProtocolBounds.TrafficKeyBytes)
        {
            throw new ArgumentException("A traffic key is 32 bytes.", nameof(trafficKey));
        }
    }
}
