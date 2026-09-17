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

        var registered = mailbox.RegisterOffer(offer, "TESTCODE12", "192.0.2.10");
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

    /// <summary>
    /// A future-dated offer is rejected rather than filling the table forever.
    /// </summary>
    /// <remarks>
    /// <see cref="PairingOffer"/> only bounds how long an offer lasts, not when it claims to have
    /// started: 64 offers dated far in the future would each hold their slot until the process
    /// restarted, since <see cref="CompanionPairingMailbox.Sweep()"/> never reaches an
    /// <c>ExpiresUtc</c> that far ahead, and every later pairing would see "invitation-limit".
    /// </remarks>
    [Fact]
    public void AnOfferDatedFarInTheFutureIsRejected()
    {
        var mailbox = new CompanionPairingMailbox(new FixedTimeProvider(Now));
        var futureOffer = MakeOffer(Now.Add(ProtocolBounds.MaxClientClockSkew).AddMinutes(5));

        var registered = mailbox.RegisterOffer(futureOffer, "TESTCODE66", "192.0.2.61");

        Assert.False(registered.Succeeded);
        Assert.Equal("pairing-rejected", registered.Code);
    }

    [Fact]
    public void RegistrationIsRateLimitedPerSource()
    {
        var mailbox = new CompanionPairingMailbox(new FixedTimeProvider(Now));

        MailboxResult<bool> last = default!;
        for (var attempt = 0; attempt < ProtocolBounds.MaxPairingAttemptsPerWindow; attempt++)
        {
            last = mailbox.RegisterOffer(MakeOffer(Now), $"REGCODE{attempt}AB", "192.0.2.70");
            Assert.True(last.Succeeded);
        }

        var rateLimited = mailbox.RegisterOffer(MakeOffer(Now), "REGCODEEXB", "192.0.2.70");
        Assert.False(rateLimited.Succeeded);
        Assert.Equal("rate-limited", rateLimited.Code);
    }

    [Fact]
    public async Task AResentIdenticalRequestIsAcceptedButAConflictingOneIsRejected()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE22", "192.0.2.21").Succeeded);

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
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE33", "192.0.2.31").Succeeded);

        var duplicate = mailbox.RegisterOffer(offer, "ANOTHRCOD3", "192.0.2.31");

        Assert.False(duplicate.Succeeded);
    }

    [Fact]
    public void AnAttemptIsSweptOnceItsOfferHasExpired()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE44", "192.0.2.41").Succeeded);

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
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE55", "192.0.2.51").Succeeded);

        Assert.False(mailbox.IsDenied(offer.AttemptId));

        var denied = mailbox.Deny(offer.AttemptId);

        Assert.True(denied.Succeeded);
        Assert.True(mailbox.IsDenied(offer.AttemptId));
    }

    /// <summary>
    /// v2r-tablet-marks-sync, ABUSE-PAIRED-LIVE-BEARER-THEFT: the relay session credential is a
    /// bearer secret, not a plaintext message like <c>established</c> — it must never be readable
    /// twice, so a second GET behaves exactly like a miss rather than replaying it.
    /// </summary>
    [Fact]
    public void ARelaySessionCredentialIsReadableExactlyOnce()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE66", "192.0.2.61").Succeeded);
        var sealedCredential = new SealedRelayCredential("nonce", "ciphertext", "tag", Now.AddMinutes(5));

        Assert.Null(mailbox.ReadRelaySession(offer.AttemptId));

        var submitted = mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential);
        Assert.True(submitted.Succeeded);

        var firstRead = mailbox.ReadRelaySession(offer.AttemptId);
        Assert.Equal(sealedCredential, firstRead);

        var secondRead = mailbox.ReadRelaySession(offer.AttemptId);
        Assert.Null(secondRead);
    }

    [Fact]
    public void ARelaySessionCredentialCannotBeSubmittedForAnUnregisteredAttempt()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var sealedCredential = new SealedRelayCredential("nonce", "ciphertext", "tag", Now.AddMinutes(5));

        var submitted = mailbox.SubmitRelaySession(new PairingAttemptId(Guid.NewGuid()), sealedCredential);

        Assert.False(submitted.Succeeded);
        Assert.Equal("pairing-rejected", submitted.Code);
    }

    /// <summary>
    /// First write wins, full stop — even resubmitting the identical value is rejected.
    /// <see cref="RelayCredentialCryptography.Seal"/> reseals with a fresh random nonce every call,
    /// so a retried registration never produces byte-identical ciphertext for the same secret; this
    /// mailbox never decrypts anything, so it has no way to recognize "the same secret, resealed"
    /// as anything other than a second, different submission.
    /// </summary>
    [Fact]
    public void ASecondRelaySessionCredentialSubmissionIsAlwaysRejectedEvenAnIdenticalOne()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE77", "192.0.2.71").Succeeded);
        var sealedCredential = new SealedRelayCredential("nonce", "ciphertext", "tag", Now.AddMinutes(5));
        Assert.True(mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential).Succeeded);

        var sameAgain = mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential);
        var different = mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential with { CiphertextBase64Url = "different" });

        Assert.False(sameAgain.Succeeded);
        Assert.False(different.Succeeded);
    }

    /// <summary>First write wins even after the first submission has already been read once.</summary>
    [Fact]
    public void ASecondRelaySessionCredentialSubmissionIsRejectedEvenAfterTheFirstWasAlreadyConsumed()
    {
        var clock = new FixedTimeProvider(Now);
        var mailbox = new CompanionPairingMailbox(clock);
        var offer = MakeOffer(Now);
        Assert.True(mailbox.RegisterOffer(offer, "TESTCODE88", "192.0.2.81").Succeeded);
        var sealedCredential = new SealedRelayCredential("nonce", "ciphertext", "tag", Now.AddMinutes(5));
        Assert.True(mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential).Succeeded);
        Assert.NotNull(mailbox.ReadRelaySession(offer.AttemptId));

        var resubmitted = mailbox.SubmitRelaySession(offer.AttemptId, sealedCredential);

        Assert.False(resubmitted.Succeeded);
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
