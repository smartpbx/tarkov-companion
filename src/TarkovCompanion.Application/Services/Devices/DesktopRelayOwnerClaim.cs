using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>The pieces a relay's owner-claim route needs to build a completed, self-contained <see cref="PairingAttempt"/>.</summary>
public sealed record DesktopRelayOwnerClaimMaterial(
    PairingOffer Offer,
    string DesktopNonceBase64Url,
    DateTimeOffset CodeConsumedUtc,
    PairingRequest Request,
    HandshakeChallenge Challenge,
    SessionEstablished Establishment)
{
    /// <summary>The COSE thumbprint the relay will register as this desktop's owner device key.</summary>
    public string DeviceKeyIdThumbprint => Request.DeviceKey.KeyId.Value;

    public PairingAttempt ToCompletedAttempt() => new(
        Offer,
        DesktopNonceBase64Url,
        PairingAttemptStage.Completed,
        CodeConsumedUtc,
        Request,
        Challenge,
        Establishment,
        Establishment.EstablishedUtc);

    /// <summary>
    /// The relay's <c>/admin/relay/claim</c> and <c>/v2/companion/relay/devices</c> wire shape:
    /// <c>{offer, desktopNonceBase64Url, codeConsumedUtc, request, challenge, establishment}</c>,
    /// with each nested field written through the same <see cref="CompanionProtocolJson"/> boundary
    /// the relay reads it back through.
    /// </summary>
    public byte[] ToJsonBody() =>
        RelayDeviceClaimWireFormat.Build(Offer, DesktopNonceBase64Url, CodeConsumedUtc, Request, Challenge, Establishment);
}

/// <summary>
/// Builds the desktop's own completed "pairing" so it can claim relay ownership (v2r-relay-owner,
/// #278). There is no second, untrusted party here — the desktop is both the approving desktop and
/// the device being registered — so this skips the two-party ceremony <see
/// cref="DesktopPairingCoordinator"/> runs for a tablet (WebAuthn device-key proof, mailbox polling,
/// verification-code confirmation) and instead builds a real, self-consistent
/// <see cref="PairingOffer"/>/<see cref="PairingRequest"/>/<see cref="SessionEstablished"/> directly
/// from <see cref="PairingCryptography"/>'s public building blocks: a genuine ECDH exchange between
/// two locally generated ephemeral keys, a genuinely sealed device name, and a real transcript hash
/// signed with nothing more than what <see cref="RelayDeviceRegistry.RecoverOwnerAsync"/> already
/// structurally requires. The relay itself never re-verifies a device-key proof for any completed
/// pairing it is handed (see its own remarks) — the trust boundary for owner recovery is the admin
/// key checked by <c>RelayAdmin.IsAuthorised</c> before this material is ever read, not a WebAuthn
/// ceremony that has no second device to run against.
/// </summary>
public static class DesktopRelayOwnerClaim
{
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(2);

    // Just inside the protocol's hard cap (HandshakeChallenge requires SessionExpiresUtc -
    // IssuedUtc <= ProtocolBounds.MaximumSessionLifetime), not exactly on it.
    //
    // [V2 rough package 48] It used to ask for exactly the maximum, which left no room for the two
    // clocks to differ: the relay derives a CSRF lifetime from this expiry minus its own now, so a
    // desktop a second ahead of the relay asked it for one second more than its own browser-session
    // bound. The relay now clamps that itself, and this stops riding the boundary in the first
    // place. Resuming an expired owner session is still not implemented.
    private static readonly TimeSpan SessionLifetime =
        ProtocolBounds.MaximumSessionLifetime - TimeSpan.FromMinutes(5);

    // {1: 2 (EC2), 3: -7 (ES256), -1: 1 (P-256), -2: bstr(32)} ... {-3: bstr(32)}, the same CTAP2
    // canonical COSE_Key layout DevicePublicKey requires (HandshakeContracts.cs).
    private static readonly byte[] CoseEs256Prefix = [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20];
    private static readonly byte[] CoseEs256YLabel = [0x22, 0x58, 0x20];

    public static DesktopRelayOwnerClaimMaterial Build(
        IDesktopIdentitySigner signer,
        CompanionDeviceId desktopDeviceId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signer);
        // Every protocol timestamp built below requires exact millisecond precision
        // (ProtocolGuard.Utc); TimeProvider.System.GetUtcNow() is sub-millisecond, so a caller
        // passing it through unmodified would fail every constructor below.
        now = MillisecondUtc(now);
        var desktopIdentityKey = signer.PublicKey;

        using var offerEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offerEphemeralPublic = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url(offerEphemeral.PublicKey.ExportSubjectPublicKeyInfo()));

        var desktopNonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var offer = new PairingOffer(
            new PairingAttemptId(Guid.NewGuid()),
            desktopIdentityKey,
            offerEphemeralPublic,
            PairingCryptography.ComputeDesktopNonceCommitment(desktopNonce),
            now,
            now.Add(OfferLifetime));

        var deviceKey = BuildOwnDeviceKey(desktopIdentityKey);
        using var deviceEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var deviceEphemeralPublic = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url(deviceEphemeral.PublicKey.ExportSubjectPublicKeyInfo()));
        var clientNonce = Base64Url(RandomNumberGenerator.GetBytes(32));

        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(offerEphemeral, deviceEphemeralPublic);
        try
        {
            var contextHash = PairingCryptography.ComputePairingRequestContextHash(
                offer,
                CompanionProtocolVersion.Current,
                deviceKey,
                deviceEphemeralPublic,
                clientNonce);
            var sealedName = PairingCryptography.SealDeviceName(sharedSecret, contextHash, "This desktop");
            var request = new PairingRequest(
                offer.AttemptId,
                CompanionProtocolVersion.Current,
                deviceKey,
                deviceEphemeralPublic,
                clientNonce,
                sealedName);
            var reveal = new PairingNonceReveal(offer.AttemptId, desktopNonce);

            var assignment = new SessionAssignment(
                CompanionProtocolVersion.Current,
                desktopDeviceId,
                new DeviceSessionId(Guid.NewGuid()),
                new RelayChannelId(Guid.NewGuid()),
                keyEpoch: 1,
                RelayCipherSuite.P256HkdfSha256Aes256Gcm,
                now.Add(SessionLifetime));
            var transcript = HandshakeTranscript.ForPairing(
                offer,
                request,
                reveal,
                new HandshakeChallengeId(Guid.NewGuid()),
                assignment,
                now,
                now.Add(OfferLifetime));
            var challenge = transcript.Sign(signer);
            var establishment = new SessionEstablished(
                transcript.ChallengeId,
                HandshakePurpose.Pairing,
                deviceKey.KeyId,
                assignment,
                transcript.ComputeHashBase64Url(),
                now);
            return new DesktopRelayOwnerClaimMaterial(offer, desktopNonce, now, request, challenge, establishment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>The same identity key, re-encoded from SPKI into the CTAP2 COSE_Key shape a relay device key needs.</summary>
    private static DevicePublicKey BuildOwnDeviceKey(DesktopIdentityKey desktopIdentityKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(FromBase64Url(desktopIdentityKey.SubjectPublicKeyInfoBase64Url), out _);
        var point = ecdsa.ExportParameters(false).Q;
        var x = point.X ?? throw new InvalidOperationException("The desktop identity key has no X coordinate.");
        var y = point.Y ?? throw new InvalidOperationException("The desktop identity key has no Y coordinate.");
        if (x.Length != 32 || y.Length != 32)
        {
            throw new InvalidOperationException("The desktop identity key is not a P-256 point.");
        }

        var cose = new byte[77];
        CoseEs256Prefix.CopyTo(cose, 0);
        x.CopyTo(cose, 10);
        CoseEs256YLabel.CopyTo(cose, 42);
        y.CopyTo(cose, 45);
        return new DevicePublicKey(
            new DeviceKeyId(Base64Url(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64Url(RandomNumberGenerator.GetBytes(16)),
            Base64Url(cose));
    }

    private static DateTimeOffset MillisecondUtc(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }
}
