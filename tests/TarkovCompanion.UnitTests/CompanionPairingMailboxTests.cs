using System.Buffers.Text;
using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// <see cref="CompanionPairingMailbox"/> only relays the pairing ceremony's plaintext messages
/// (docs/PAIRED_DEVICE_PROTOCOL.md, "Pairing"); the ceremony's own cryptographic decisions are
/// <see cref="DesktopPairingCoordinator"/>'s and are covered by its own tests. These tests are
/// about what the mailbox adds: one-time code consumption, rate limiting, expiry, and refusing to
/// let a second sender silently overwrite a step the other side may already have read.
/// </summary>
public sealed class CompanionPairingMailboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnOfferIsResolvedOnceAndTheCodeCannotBeReusedAfterward()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);

        var registered = mailbox.RegisterOffer(offer, "TESTCODE12");
        Assert.True(registered.Succeeded);

        var resolved = mailbox.ResolveOffer("test-code-12", "192.0.2.10");
        Assert.True(resolved.Succeeded);
        Assert.Equal(offer, resolved.Value);

        var reused = mailbox.ResolveOffer("TESTCODE12", "192.0.2.10");
        Assert.False(reused.Succeeded);
        Assert.Equal("pairing-rejected", reused.Code);
    }

    [Fact]
    public void ResolvingAnUnregisteredCodeIsRejectedRatherThanThrowing()
    {
        var mailbox = new CompanionPairingMailbox(new FixedTimeProvider(Now));

        var resolved = mailbox.ResolveOffer("ABSENTCOD3", "192.0.2.10");

        Assert.False(resolved.Succeeded);
        Assert.Equal("pairing-rejected", resolved.Code);
    }

    [Fact]
    public void ResolveIsRateLimitedPerSource()
    {
        var mailbox = new CompanionPairingMailbox(new FixedTimeProvider(Now));

        MailboxResult<PairingOffer> last = default!;
        for (var attempt = 0; attempt < ProtocolBounds.MaxPairingAttemptsPerWindow; attempt++)
        {
            last = mailbox.ResolveOffer("WRONGCODE1", "192.0.2.20");
            Assert.Equal("pairing-rejected", last.Code);
        }

        var rateLimited = mailbox.ResolveOffer("WRONGCODE1", "192.0.2.20");
        Assert.False(rateLimited.Succeeded);
        Assert.Equal("rate-limited", rateLimited.Code);
    }

    [Fact]
    public async Task AResentIdenticalRequestIsAcceptedButAConflictingOneIsRejected()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE22").Succeeded);

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicDeviceKey = PublicDeviceKey(deviceKey);

        using var firstEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var firstRequest = await PairingRequestFor(offer, firstEphemeral, publicDeviceKey, "Raid tablet");

        var firstSubmit = mailbox.SubmitRequest(offer.AttemptId, firstRequest);
        Assert.True(firstSubmit.Succeeded);

        // The same device resending its own request on a transport retry.
        var resent = mailbox.SubmitRequest(offer.AttemptId, firstRequest);
        Assert.True(resent.Succeeded);
        Assert.Equal(firstRequest, mailbox.ReadRequest(offer.AttemptId));

        using var secondEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var conflictingRequest = await PairingRequestFor(offer, secondEphemeral, publicDeviceKey, "Somebody else's tablet");

        var conflict = mailbox.SubmitRequest(offer.AttemptId, conflictingRequest);
        Assert.False(conflict.Succeeded);
        Assert.Equal(firstRequest, mailbox.ReadRequest(offer.AttemptId));
    }

    [Fact]
    public void RegisteringTheSameAttemptTwiceIsRejected()
    {
        var mailbox = new CompanionPairingMailbox(new FixedTimeProvider(Now));
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE33").Succeeded);

        var duplicate = mailbox.RegisterOffer(offer, "ANOTHRCOD3");

        Assert.False(duplicate.Succeeded);
    }

    [Fact]
    public void AnAttemptIsSweptOnceItsOfferHasExpired()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE44").Succeeded);

        clock.Advance(ProtocolBounds.PairingLifetime + TimeSpan.FromMinutes(2));

        var resolvedAfterExpiry = mailbox.ResolveOffer("TESTCODE44", "192.0.2.30");
        Assert.False(resolvedAfterExpiry.Succeeded);
        Assert.Null(mailbox.ReadRequest(offer.AttemptId));
    }

    [Fact]
    public void DenyingAnAttemptIsVisibleToAWaitingTablet()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE55").Succeeded);

        Assert.False(mailbox.IsDenied(offer.AttemptId));

        var denied = mailbox.Deny(offer.AttemptId);

        Assert.True(denied.Succeeded);
        Assert.True(mailbox.IsDenied(offer.AttemptId));
    }

    private static PairingOffer MakeOffer(DateTimeOffset now)
    {
        using var identityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identitySpki = identityKey.ExportSubjectPublicKeyInfo();
        var desktopIdentityKey = new DesktopIdentityKey(
            new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(identitySpki))),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            Base64Url.EncodeToString(identitySpki));
        var desktopEphemeralKey = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url.EncodeToString(ephemeralKey.ExportSubjectPublicKeyInfo()));
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));

        return PairingStateMachine.Offer(
            new PairingAttemptId(Guid.NewGuid()),
            desktopIdentityKey,
            desktopEphemeralKey,
            nonce,
            now).Offer;
    }

    private static DevicePublicKey PublicDeviceKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        byte[] cose =
        [
            0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20,
            .. parameters.Q.X!,
            0x22, 0x58, 0x20,
            .. parameters.Q.Y!,
        ];
        return new DevicePublicKey(
            new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64Url.EncodeToString(SHA256.HashData(cose)[..16]),
            Base64Url.EncodeToString(cose));
    }

    private static Task<PairingRequest> PairingRequestFor(
        PairingOffer offer,
        ECDiffieHellman tabletEphemeralKey,
        DevicePublicKey deviceKey,
        string deviceName)
    {
        var ephemeralPublicKey = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url.EncodeToString(tabletEphemeralKey.ExportSubjectPublicKeyInfo()));
        var clientNonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(tabletEphemeralKey, offer.DesktopEphemeralKey);
        try
        {
            var context = PairingCryptography.ComputePairingRequestContextHash(
                offer,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce);
            return Task.FromResult(new PairingRequest(
                offer.AttemptId,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce,
                PairingCryptography.SealDeviceName(sharedSecret, context, deviceName)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now = _now.Add(elapsed);
    }
}
