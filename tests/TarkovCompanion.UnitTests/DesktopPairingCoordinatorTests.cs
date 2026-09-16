using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Infrastructure.Devices;

namespace TarkovCompanion.UnitTests;

public sealed class DesktopPairingCoordinatorTests
{
    private const string RelyingPartyId = "companion.example";
    private const string Origin = "https://tablet.companion.example";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnverifiedProtocolAggregatesCannotBypassTheCoordinatorRegistrationBoundary()
    {
        Assert.Null(typeof(DesktopCompanionAuthority).GetMethod("RegisterPairingAsync"));
        Assert.Null(typeof(DesktopCompanionAuthority).GetMethod("RegisterResumedSessionAsync"));
    }

    [Fact]
    public async Task PairResumeAndRevokeRequireEveryCeremonyBoundary()
    {
        using var desktopSigner = new FixtureDesktopSigner();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicDeviceKey = PublicDeviceKey(deviceKey);
        var counterStore = new MemoryCounterStore();
        var verifier = new WebAuthnDeviceKeyProofVerifier(RelyingPartyId, Origin, counterStore);
        var authorityStore = new MemoryAuthorityStore();
        using var authority = await DesktopCompanionAuthority.OpenAsync(authorityStore, InitialState());
        using var coordinator = new DesktopPairingCoordinator(authority, desktopSigner, verifier);

        var invitation = await coordinator.CreateInvitationAsync(Now);
        var resolved = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            IPAddress.Parse("192.0.2.10"),
            Now.AddSeconds(1));
        Assert.Equal(PairingOfferResolutionStatus.Accepted, resolved.Status);
        Assert.Equal(invitation.Offer, resolved.Offer);

        var reused = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            IPAddress.Parse("192.0.2.10"),
            Now.AddSeconds(2));
        Assert.Equal(PairingOfferResolutionStatus.NotFound, reused.Status);

        using var tabletPairingKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pairingRequest = PairingRequestFor(
            invitation.Offer,
            tabletPairingKey,
            publicDeviceKey,
            "Raid tablet");
        var approval = await coordinator.BindRequestAsync(
            pairingRequest,
            CompanionProtocolVersion.Current,
            Now.AddSeconds(3));
        Assert.Equal("Raid tablet", approval.RequestedDisplayName);
        Assert.Equal(ProtocolBounds.VerificationCodeDigits, approval.VerificationCode.Length);
        Assert.Equal(
            approval,
            await coordinator.BindRequestAsync(
                pairingRequest,
                CompanionProtocolVersion.Current,
                Now.AddSeconds(3).AddMilliseconds(1)));
        using (var replacementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            var replacement = PairingRequestFor(
                invitation.Offer,
                replacementKey,
                publicDeviceKey,
                "Replacement tablet");
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await coordinator.BindRequestAsync(
                    replacement,
                    CompanionProtocolVersion.Current,
                    Now.AddSeconds(3).AddMilliseconds(2)));
        }

        var grant = Grant(Now.AddDays(30));
        var challenge = await coordinator.ApproveAsync(
            invitation.Offer.AttemptId,
            userConfirmedMatchingVerificationCode: true,
            grant,
            Now.AddSeconds(4));
        Assert.Equal(
            challenge,
            await coordinator.ApproveAsync(
                invitation.Offer.AttemptId,
                userConfirmedMatchingVerificationCode: true,
                grant,
                Now.AddSeconds(4).AddMilliseconds(1)));
        var wrongOrigin = Proof(deviceKey, challenge, signatureCounter: 1, "https://evil.example");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.CompletePairingAsync(
                invitation.Offer.AttemptId,
                wrongOrigin,
                Now.AddSeconds(5)));

        var proof = Proof(deviceKey, challenge, signatureCounter: 1, Origin);
        using var paired = await coordinator.CompletePairingAsync(
            invitation.Offer.AttemptId,
            proof,
            Now.AddSeconds(6));
        var pairedDevice = Assert.Single(authority.Snapshot.Devices);
        Assert.Equal(DeviceLifecycleStatus.Active, pairedDevice.Status);
        Assert.Equal("Raid tablet", pairedDevice.DisplayName);
        Assert.Equal(ProtocolBounds.TrafficKeyBytes, paired.TabletToDesktopKey.Length);
        Assert.Equal(ProtocolBounds.TrafficKeyBytes, paired.DesktopToTabletKey.Length);
        AssertTrafficKeysMatch(tabletPairingKey, challenge, paired);

        using var tabletResumeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var resumeRequest = ResumeRequestFor(pairedDevice, tabletResumeKey);
        var resumeChallenge = await coordinator.BeginResumeAsync(
            resumeRequest,
            CompanionProtocolVersion.Current,
            pairedDevice.Capabilities,
            CompanionTransportKind.EndToEndRelay,
            CompanionSurfaceKind.TabletLandscape,
            Now.AddMinutes(1));
        Assert.Equal(
            resumeChallenge,
            await coordinator.BeginResumeAsync(
                resumeRequest,
                CompanionProtocolVersion.Current,
                pairedDevice.Capabilities,
                CompanionTransportKind.EndToEndRelay,
                CompanionSurfaceKind.TabletLandscape,
                Now.AddMinutes(1).AddSeconds(1)));
        using (var replacementResumeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            var replacementResume = ResumeRequestFor(pairedDevice, replacementResumeKey);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await coordinator.BeginResumeAsync(
                    replacementResume,
                    CompanionProtocolVersion.Current,
                    pairedDevice.Capabilities,
                    CompanionTransportKind.EndToEndRelay,
                    CompanionSurfaceKind.TabletLandscape,
                    Now.AddMinutes(1).AddSeconds(1).AddMilliseconds(1)));
        }

        var replayedCounter = Proof(deviceKey, resumeChallenge, signatureCounter: 1, Origin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.CompleteResumeAsync(
                resumeChallenge.ChallengeId,
                replayedCounter,
                Now.AddMinutes(1).AddSeconds(2)));

        var freshCounter = Proof(deviceKey, resumeChallenge, signatureCounter: 2, Origin);
        using var resumed = await coordinator.CompleteResumeAsync(
            resumeChallenge.ChallengeId,
            freshCounter,
            Now.AddMinutes(1).AddSeconds(3));
        Assert.Equal(2, authority.Snapshot.Devices[0].LastKeyEpoch);
        Assert.Single(authority.Snapshot.Sessions, session => session.Status == DeviceSessionStatus.Active);
        Assert.Single(authority.Snapshot.Sessions, session => session.Status == DeviceSessionStatus.Replaced);
        AssertTrafficKeysMatch(tabletResumeKey, resumeChallenge, resumed);

        _ = await coordinator.RevokeDeviceAsync(
            pairedDevice.DeviceId,
            Now.AddMinutes(2),
            "stolen-device");
        using var afterRevocationKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var stolenRequest = ResumeRequestFor(authority.Snapshot.Devices[0], afterRevocationKey);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.BeginResumeAsync(
                stolenRequest,
                CompanionProtocolVersion.Current,
                pairedDevice.Capabilities,
                CompanionTransportKind.EndToEndRelay,
                CompanionSurfaceKind.TabletLandscape,
                Now.AddMinutes(2).AddSeconds(1)));
    }

    [Fact]
    public async Task InvalidFirstBoundRequestConsumesAttemptInsteadOfAllowingReplacement()
    {
        using var desktopSigner = new FixtureDesktopSigner();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var authority = await DesktopCompanionAuthority.OpenAsync(
            new MemoryAuthorityStore(),
            InitialState());
        using var coordinator = new DesktopPairingCoordinator(
            authority,
            desktopSigner,
            new WebAuthnDeviceKeyProofVerifier(RelyingPartyId, Origin, new MemoryCounterStore()));
        var invitation = await coordinator.CreateInvitationAsync(Now);
        _ = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            IPAddress.Loopback,
            Now.AddSeconds(1));
        using var tabletKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var valid = PairingRequestFor(
            invitation.Offer,
            tabletKey,
            PublicDeviceKey(deviceKey),
            "Raid tablet");
        var tag = Base64Url.DecodeFromChars(valid.RequestedDeviceName.AuthenticationTagBase64Url);
        tag[0] ^= 0x80;
        var tampered = new PairingRequest(
            valid.AttemptId,
            valid.NegotiatedVersion,
            valid.DeviceKey,
            valid.EphemeralKey,
            valid.ClientNonceBase64Url,
            new SealedDeviceName(
                valid.RequestedDeviceName.CiphertextBase64Url,
                Base64Url.EncodeToString(tag)));

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(async () =>
            await coordinator.BindRequestAsync(
                tampered,
                CompanionProtocolVersion.Current,
                Now.AddSeconds(2)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.BindRequestAsync(
                valid,
                CompanionProtocolVersion.Current,
                Now.AddSeconds(3)));
    }

    [Fact]
    public async Task MismatchedLocalCodeConfirmationConsumesTheAttempt()
    {
        using var desktopSigner = new FixtureDesktopSigner();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new WebAuthnDeviceKeyProofVerifier(
            RelyingPartyId,
            Origin,
            new MemoryCounterStore());
        using var authority = await DesktopCompanionAuthority.OpenAsync(
            new MemoryAuthorityStore(),
            InitialState());
        using var coordinator = new DesktopPairingCoordinator(authority, desktopSigner, verifier);
        var invitation = await coordinator.CreateInvitationAsync(Now);
        _ = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            IPAddress.Loopback,
            Now.AddSeconds(1));
        using var tabletKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _ = await coordinator.BindRequestAsync(
            PairingRequestFor(invitation.Offer, tabletKey, PublicDeviceKey(deviceKey), "Untrusted tablet"),
            CompanionProtocolVersion.Current,
            Now.AddSeconds(2));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.ApproveAsync(
                invitation.Offer.AttemptId,
                userConfirmedMatchingVerificationCode: false,
                Grant(Now.AddDays(30)),
                Now.AddSeconds(3)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await coordinator.ApproveAsync(
                invitation.Offer.AttemptId,
                userConfirmedMatchingVerificationCode: true,
                Grant(Now.AddDays(30)),
                Now.AddSeconds(4)));
        Assert.Empty(authority.Snapshot.Devices);
    }

    [Fact]
    public async Task PairingLookupsAreSourceRateLimitedAndExpiredOffersDisappear()
    {
        using var desktopSigner = new FixtureDesktopSigner();
        using var authority = await DesktopCompanionAuthority.OpenAsync(
            new MemoryAuthorityStore(),
            InitialState());
        using var coordinator = new DesktopPairingCoordinator(
            authority,
            desktopSigner,
            new WebAuthnDeviceKeyProofVerifier(RelyingPartyId, Origin, new MemoryCounterStore()));
        var invitation = await coordinator.CreateInvitationAsync(Now);
        var source = IPAddress.Parse("198.51.100.20");

        for (var attempt = 0; attempt < ProtocolBounds.MaxPairingAttemptsPerWindow; attempt++)
        {
            var missingCode = $"ZZZZZZZZZ{attempt}";
            if (string.Equals(missingCode, invitation.PairingCode, StringComparison.Ordinal))
            {
                missingCode = $"YZZZZZZZZ{attempt}";
            }

            var missing = await coordinator.ResolveOfferAsync(
                missingCode,
                source,
                Now.AddSeconds(attempt));
            Assert.Equal(PairingOfferResolutionStatus.NotFound, missing.Status);
        }

        var limited = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            source,
            Now.AddSeconds(10));
        Assert.Equal(PairingOfferResolutionStatus.RateLimited, limited.Status);
        Assert.NotNull(limited.RetryAfterUtc);

        var expired = await coordinator.ResolveOfferAsync(
            invitation.PairingCode,
            IPAddress.Parse("198.51.100.21"),
            Now.Add(ProtocolBounds.PairingLifetime));
        Assert.Equal(PairingOfferResolutionStatus.NotFound, expired.Status);
    }

    [Fact]
    public async Task PersistentCounterStoreIsMonotonicIdempotentAndFailsClosedOnCorruption()
    {
        var directory = Directory.CreateTempSubdirectory("tarkov-companion-webauthn-counter-");
        try
        {
            var path = Path.Combine(directory.FullName, "counters.json");
            var keyId = new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData("counter-key"u8)));
            var firstChallenge = new HandshakeChallengeId(Guid.NewGuid());
            var secondChallenge = new HandshakeChallengeId(Guid.NewGuid());
            using (var store = new JsonFileDeviceSignatureCounterStore(path))
            {
                Assert.True(await store.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, firstChallenge, 7, Now),
                    CancellationToken.None));
                Assert.True(await store.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, firstChallenge, 7, Now),
                    CancellationToken.None));
                Assert.False(await store.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, firstChallenge, 7, Now.AddMilliseconds(1)),
                    CancellationToken.None));
                Assert.False(await store.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, secondChallenge, 7, Now.AddSeconds(1)),
                    CancellationToken.None));
                Assert.True(await store.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, secondChallenge, 8, Now.AddSeconds(1)),
                    CancellationToken.None));
            }

            using (var reopened = new JsonFileDeviceSignatureCounterStore(path))
            {
                Assert.False(await reopened.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, new HandshakeChallengeId(Guid.NewGuid()), 8, Now.AddSeconds(2)),
                    CancellationToken.None));
            }

            await File.WriteAllTextAsync(path, "{\"formatVersion\":1,\"formatVersion\":1,\"counters\":[]}");
            using var corrupt = new JsonFileDeviceSignatureCounterStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await corrupt.TryAcceptAsync(
                    new DeviceSignatureCounter(keyId, new HandshakeChallengeId(Guid.NewGuid()), 9, Now.AddSeconds(3)),
                    CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PairingDeviceGrant Grant(DateTimeOffset expiresUtc) => new(
        DeviceAuthorizationRole.Member,
        [
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
        ],
        [DeviceCapability.FollowDesktop, DeviceCapability.ShowOnDesktop],
        expiresUtc,
        CompanionTransportKind.EndToEndRelay,
        CompanionSurfaceKind.TabletLandscape);

    private static PairingRequest PairingRequestFor(
        PairingOffer offer,
        ECDiffieHellman tabletEphemeralKey,
        DevicePublicKey deviceKey,
        string deviceName)
    {
        var ephemeralPublicKey = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url.EncodeToString(tabletEphemeralKey.ExportSubjectPublicKeyInfo()));
        var clientNonce = Base64Url.EncodeToString(
            RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(tabletEphemeralKey, offer.DesktopEphemeralKey);
        try
        {
            var context = PairingCryptography.ComputePairingRequestContextHash(
                offer,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce);
            return new PairingRequest(
                offer.AttemptId,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce,
                PairingCryptography.SealDeviceName(sharedSecret, context, deviceName));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static SessionResumeRequest ResumeRequestFor(
        PairedDevice device,
        ECDiffieHellman tabletEphemeralKey) => new(
            CompanionProtocolVersion.Current,
            device.DeviceId,
            device.DeviceKey.KeyId,
            device.DeviceKey.CredentialIdBase64Url,
            new EphemeralPublicKey(
                EphemeralKeyAlgorithm.EcdhP256,
                Base64Url.EncodeToString(tabletEphemeralKey.ExportSubjectPublicKeyInfo())),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes)));

    private static DeviceKeyProof Proof(
        ECDsa deviceKey,
        HandshakeChallenge challenge,
        uint signatureCounter,
        string origin)
    {
        var authenticatorData = new byte[ProtocolBounds.MinAuthenticatorDataBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(RelyingPartyId)).CopyTo(authenticatorData, 0);
        authenticatorData[32] = 0x05;
        BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33, 4), signatureCounter);
        var clientData = Encoding.UTF8.GetBytes(
            $"{{\"type\":\"webauthn.get\",\"challenge\":\"{challenge.TranscriptHashBase64Url}\",\"origin\":\"{origin}\",\"crossOrigin\":false}}");
        var clientHash = SHA256.HashData(clientData);
        var signedData = new byte[authenticatorData.Length + clientHash.Length];
        authenticatorData.CopyTo(signedData, 0);
        clientHash.CopyTo(signedData, authenticatorData.Length);
        var signature = deviceKey.SignData(
            signedData,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return new DeviceKeyProof(
            challenge.ChallengeId,
            new WebAuthnAssertion(
                PublicDeviceKey(deviceKey).CredentialIdBase64Url,
                Base64Url.EncodeToString(authenticatorData),
                Base64Url.EncodeToString(clientData),
                Base64Url.EncodeToString(signature)));
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

    private static void AssertTrafficKeysMatch(
        ECDiffieHellman tabletEphemeralKey,
        HandshakeChallenge challenge,
        EstablishedDesktopSession desktopSession)
    {
        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(
            tabletEphemeralKey,
            challenge.DesktopEphemeralKey);
        try
        {
            var tabletToDesktop = PairingCryptography.DeriveTrafficKey(
                sharedSecret,
                challenge.TranscriptHashBase64Url,
                PairingTrafficDirection.TabletToDesktop);
            var desktopToTablet = PairingCryptography.DeriveTrafficKey(
                sharedSecret,
                challenge.TranscriptHashBase64Url,
                PairingTrafficDirection.DesktopToTablet);
            Assert.True(tabletToDesktop.AsSpan().SequenceEqual(desktopSession.TabletToDesktopKey.Span));
            Assert.True(desktopToTablet.AsSpan().SequenceEqual(desktopSession.DesktopToTabletKey.Span));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static CanonicalCompanionState InitialState()
    {
        var workspace = new WorkspaceId(Guid.Parse("80000000-0000-4000-8000-000000000001"));
        return new CanonicalCompanionState(
            new AuthorityEpoch(Guid.Parse("30000000-0000-4000-8000-000000000001")),
            workspace,
            "desktop-install-1",
            new GlobalRevision(0),
            new CompanionDeviceId(Guid.Parse("10000000-0000-4000-8000-000000000001")),
            new DeviceModeAggregate(AggregateCursor.Empty, [], null, null),
            new WorkspaceAggregate(
                AggregateCursor.Empty,
                new WorkspaceProjection(
                    WorkspaceKind.Raid,
                    "customs",
                    "ground",
                    new WorkspaceViewport(
                        new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 10, 2, 20),
                        1),
                    null,
                    [],
                    [],
                    null,
                    [],
                    ["extracts"],
                    [],
                    null)),
            new MarkAggregate(AggregateCursor.Empty, []),
            new CaptureIntentAggregate(AggregateCursor.Empty, null),
            ProfilePreferencesAggregate.Empty);
    }

    private sealed class FixtureDesktopSigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public FixtureDesktopSigner()
        {
            var publicKey = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(publicKey))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                Base64Url.EncodeToString(publicKey));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) => _key.SignData(
            signatureInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }

    private sealed class MemoryCounterStore : IDeviceSignatureCounterStore
    {
        private readonly Dictionary<DeviceKeyId, DeviceSignatureCounter> _counters = [];

        public ValueTask<bool> TryAcceptAsync(
            DeviceSignatureCounter candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_counters.TryGetValue(candidate.DeviceKeyId, out var existing))
            {
                if (existing.ChallengeId == candidate.ChallengeId)
                {
                    return ValueTask.FromResult(
                        existing.SignatureCounter == candidate.SignatureCounter &&
                        existing.ChallengeIssuedUtc == candidate.ChallengeIssuedUtc);
                }

                if (existing.SignatureCounter > 0 && candidate.SignatureCounter <= existing.SignatureCounter)
                {
                    return ValueTask.FromResult(false);
                }
            }

            _counters[candidate.DeviceKeyId] = candidate;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class MemoryAuthorityStore : IDesktopCompanionAuthorityStore
    {
        private DesktopCompanionAuthorityState? _state;
        private int _leased;

        public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _leased, 1) != 0)
            {
                throw new IOException("fixture authority is already leased");
            }

            return ValueTask.FromResult<IDisposable>(new Lease(this));
        }

        public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_state);
        }

        public ValueTask SaveAsync(DesktopCompanionAuthorityState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = state;
            return ValueTask.CompletedTask;
        }

        private sealed class Lease(MemoryAuthorityStore owner) : IDisposable
        {
            private MemoryAuthorityStore? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    Volatile.Write(ref owner._leased, 0);
                }
            }
        }
    }
}
