using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

internal sealed class RelayTestClock : TimeProvider
{
    public RelayTestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
}

internal sealed record RelayTestContext(
    RelayTestClock Clock,
    OwnerRecoveryProtector Recovery,
    RelayDeviceRegistry Registry,
    RelaySessionCredential OwnerCredential) : IDisposable
{
    public void Dispose() => Recovery.Dispose();

    public async ValueTask<RelayPrincipal> AuthenticateOwnerAsync()
    {
        var result = await Registry.AuthenticateAsync(OwnerCredential.SessionId, OwnerCredential.Secret);
        return Assert.IsType<RelayPrincipal>(result.Principal);
    }
}

internal static class RelaySecurityTestFactory
{
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public static async ValueTask<RelayTestContext> BootstrapAsync(string? storePath = null)
    {
        var clock = new RelayTestClock(Now);
        var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        var store = storePath is null ? null : new VerifiedRelayRegistryStore(storePath);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery, store);
        var key = DeviceKey("owner");
        var ownerId = new CompanionDeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var grant = recovery.CreateGrant(ownerId, key.KeyId);
        var recovered = await registry.RecoverOwnerAsync(
            grant,
            "Desktop owner",
            key,
            new RelayChannelId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            CompanionSurfaceKind.Desktop);
        return new RelayTestContext(clock, recovery, registry, Assert.IsType<RelaySessionCredential>(recovered.Value));
    }

    public static DevicePublicKey DeviceKey(string suffix) => new(
        new DeviceKeyId(Base64Url($"thumbprint-{suffix}")),
        DeviceKeyAlgorithm.WebAuthnEs256,
        Base64Url($"credential-{suffix}"),
        Base64Url($"cose-public-key-{suffix}"));

    public static async ValueTask<PairingAttempt> CompletedPairingAsync(
        string suffix,
        DateTimeOffset now,
        bool proofValid = true)
    {
        var attemptId = new PairingAttemptId(Guid.NewGuid());
        var request = new PairingRequest(
            attemptId,
            $"Device {suffix}",
            DeviceKey(suffix),
            new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64Url($"ephemeral-{suffix}")),
            Base64Url($"nonce-{suffix}"));
        var bound = PairingStateMachine.BindResolvedCode(PairingStateMachine.Offer(attemptId, now), request, now);
        var challenge = Challenge(bound, now);
        var approved = PairingStateMachine.Approve(bound, challenge, now);
        return await PairingStateMachine.CompleteAsync(
            approved,
            new PairingProof(challenge.ChallengeId, Base64Url($"signature-{suffix}")),
            new ProofVerifier(proofValid),
            now);
    }

    public static PairingChallenge Challenge(PairingAttempt attempt, DateTimeOffset now) => new(
        $"challenge-{attempt.AttemptId.Value:N}",
        attempt.AttemptId,
        attempt.Request!.DeviceKey.KeyId,
        Base64Url("challenge-bytes"),
        new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64Url("desktop-ephemeral")),
        now,
        new[] { now.AddMinutes(1), attempt.ExpiresUtc }.Min());

    public static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private sealed class ProofVerifier(bool result) : IDeviceKeyProofVerifier
    {
        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            PairingChallenge challenge,
            PairingProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }
}
