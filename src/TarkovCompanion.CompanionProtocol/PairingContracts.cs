using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum DeviceKeyAlgorithm
{
    WebAuthnEs256 = 1,
}

public enum EphemeralKeyAlgorithm
{
    EcdhP256 = 1,
}

public sealed record DevicePublicKey(
    DeviceKeyId KeyId,
    DeviceKeyAlgorithm Algorithm,
    string CredentialIdBase64Url,
    string CosePublicKeyBase64Url)
{
    public DeviceKeyId KeyId { get; } = string.IsNullOrWhiteSpace(KeyId.Value)
        ? throw new ArgumentException("A device key id is required.", nameof(KeyId))
        : KeyId;

    public DeviceKeyAlgorithm Algorithm { get; } = ProtocolGuard.Defined(Algorithm, nameof(Algorithm));

    public string CredentialIdBase64Url { get; } = ProtocolGuard.Base64Url(
        CredentialIdBase64Url,
        nameof(CredentialIdBase64Url),
        512);

    public string CosePublicKeyBase64Url { get; } = ProtocolGuard.Base64Url(
        CosePublicKeyBase64Url,
        nameof(CosePublicKeyBase64Url),
        1024);
}

public sealed record EphemeralPublicKey(EphemeralKeyAlgorithm Algorithm, string SubjectPublicKeyInfoBase64Url)
{
    public EphemeralKeyAlgorithm Algorithm { get; } = ProtocolGuard.Defined(Algorithm, nameof(Algorithm));

    public string SubjectPublicKeyInfoBase64Url { get; } = ProtocolGuard.Base64Url(
        SubjectPublicKeyInfoBase64Url,
        nameof(SubjectPublicKeyInfoBase64Url),
        512);
}

public enum PairingAttemptStage
{
    Offered = 1,
    AwaitingDesktopApproval,
    AwaitingDeviceProof,
    Completed,
    Denied,
    Expired,
}

/// <summary>
/// The JSON pairing body deliberately omits the human short code. A transport resolves that
/// one-time code from a redacted header, rate limits it, and then binds this public key to the
/// attempt. Desktop approval and proof of the bound device key are still required.
/// </summary>
public sealed record PairingRequest(
    PairingAttemptId AttemptId,
    string RequestedDeviceName,
    DevicePublicKey DeviceKey,
    EphemeralPublicKey EphemeralKey,
    string ClientNonceBase64Url)
{
    public PairingAttemptId AttemptId { get; } = AttemptId.Value == Guid.Empty
        ? throw new ArgumentException("A pairing attempt id is required.", nameof(AttemptId))
        : AttemptId;

    public string RequestedDeviceName { get; } = ProtocolGuard.Required(
        RequestedDeviceName,
        nameof(RequestedDeviceName),
        ProtocolBounds.MaxShortStringBytes);

    public DevicePublicKey DeviceKey { get; } = ProtocolGuard.NotNull(DeviceKey, nameof(DeviceKey));

    public EphemeralPublicKey EphemeralKey { get; } = ProtocolGuard.NotNull(EphemeralKey, nameof(EphemeralKey));

    public string ClientNonceBase64Url { get; } = ProtocolGuard.Base64Url(
        ClientNonceBase64Url,
        nameof(ClientNonceBase64Url),
        128);
}

public sealed record PairingChallenge(
    string ChallengeId,
    PairingAttemptId AttemptId,
    DeviceKeyId DeviceKeyId,
    string ChallengeBase64Url,
    EphemeralPublicKey DesktopEphemeralKey,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset ExpiresUtc)
{
    public string ChallengeId { get; } = ProtocolGuard.Required(
        ChallengeId,
        nameof(ChallengeId),
        ProtocolBounds.MaxShortStringBytes);

    public PairingAttemptId AttemptId { get; } = AttemptId.Value == Guid.Empty
        ? throw new ArgumentException("A pairing attempt id is required.", nameof(AttemptId))
        : AttemptId;

    public DeviceKeyId DeviceKeyId { get; } = string.IsNullOrWhiteSpace(DeviceKeyId.Value)
        ? throw new ArgumentException("A device key id is required.", nameof(DeviceKeyId))
        : DeviceKeyId;

    public string ChallengeBase64Url { get; } = ProtocolGuard.Base64Url(
        ChallengeBase64Url,
        nameof(ChallengeBase64Url),
        256);

    public EphemeralPublicKey DesktopEphemeralKey { get; } =
        ProtocolGuard.NotNull(DesktopEphemeralKey, nameof(DesktopEphemeralKey));

    public DateTimeOffset ApprovedUtc { get; } = ProtocolGuard.Utc(ApprovedUtc, nameof(ApprovedUtc));

    public DateTimeOffset ExpiresUtc { get; } =
        ProtocolGuard.Utc(ExpiresUtc, nameof(ExpiresUtc)) <= ApprovedUtc ||
        ExpiresUtc - ApprovedUtc > ProtocolBounds.MaximumPairingLifetime
        ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc))
        : ExpiresUtc;
}

public sealed record PairingProof(string ChallengeId, string SignatureBase64Url)
{
    public string ChallengeId { get; } = ProtocolGuard.Required(
        ChallengeId,
        nameof(ChallengeId),
        ProtocolBounds.MaxShortStringBytes);

    public string SignatureBase64Url { get; } = ProtocolGuard.Base64Url(
        SignatureBase64Url,
        nameof(SignatureBase64Url),
        512);
}

public sealed record PairingAttempt
{
    public PairingAttempt(
        PairingAttemptId attemptId,
        PairingAttemptStage stage,
        DateTimeOffset offeredUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? codeConsumedUtc = null,
        PairingRequest? request = null,
        PairingChallenge? challenge = null,
        DateTimeOffset? endedUtc = null)
    {
        AttemptId = attemptId.Value == Guid.Empty
            ? throw new ArgumentException("A pairing attempt id is required.", nameof(attemptId))
            : attemptId;
        Stage = ProtocolGuard.Defined(stage, nameof(stage));
        OfferedUtc = ProtocolGuard.Utc(offeredUtc, nameof(offeredUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        CodeConsumedUtc = ProtocolGuard.UtcOptional(codeConsumedUtc, nameof(codeConsumedUtc));
        Request = request;
        Challenge = challenge;
        EndedUtc = ProtocolGuard.UtcOptional(endedUtc, nameof(endedUtc));

        if (ExpiresUtc <= OfferedUtc || ExpiresUtc - OfferedUtc > ProtocolBounds.MaximumPairingLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A pairing offer expires within ten minutes.");
        }

        if (request is not null && request.AttemptId != attemptId)
        {
            throw new ArgumentException("The request belongs to another pairing attempt.", nameof(request));
        }

        if (challenge is not null &&
            (challenge.AttemptId != attemptId || request is null || challenge.DeviceKeyId != request.DeviceKey.KeyId))
        {
            throw new ArgumentException("The challenge must bind this attempt and requested device key.", nameof(challenge));
        }

        if (new[] { CodeConsumedUtc, challenge?.ApprovedUtc, EndedUtc }
            .OfType<DateTimeOffset>()
            .Any(value => value < OfferedUtc || value > ExpiresUtc))
        {
            throw new ArgumentException("Pairing transition times must stay inside the offer lifetime.");
        }

        var validShape = stage switch
        {
            PairingAttemptStage.Offered =>
                request is null && codeConsumedUtc is null && challenge is null && endedUtc is null,
            PairingAttemptStage.AwaitingDesktopApproval =>
                request is not null && codeConsumedUtc is not null && challenge is null && endedUtc is null,
            PairingAttemptStage.AwaitingDeviceProof =>
                request is not null && codeConsumedUtc is not null && challenge is not null && endedUtc is null,
            PairingAttemptStage.Completed =>
                request is not null && codeConsumedUtc is not null && challenge is not null && endedUtc is not null,
            PairingAttemptStage.Denied =>
                request is not null && codeConsumedUtc is not null && challenge is null && endedUtc is not null,
            PairingAttemptStage.Expired => endedUtc is not null &&
                ((request is null && codeConsumedUtc is null && challenge is null) ||
                 (request is not null && codeConsumedUtc is not null &&
                  (challenge is null || challenge.DeviceKeyId == request.DeviceKey.KeyId))),
            _ => false,
        };

        if (!validShape)
        {
            throw new ArgumentException($"The pairing fields are inconsistent with stage {stage}.");
        }
    }

    public PairingAttemptId AttemptId { get; }

    public PairingAttemptStage Stage { get; }

    public DateTimeOffset OfferedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>The one-time lookup code ceased to be usable at this instant; the code is never stored here.</summary>
    public DateTimeOffset? CodeConsumedUtc { get; }

    public PairingRequest? Request { get; }

    public PairingChallenge? Challenge { get; }

    public DateTimeOffset? EndedUtc { get; }
}

public interface IDeviceKeyProofVerifier
{
    ValueTask<bool> VerifyAsync(
        DevicePublicKey deviceKey,
        PairingChallenge challenge,
        PairingProof proof,
        CancellationToken cancellationToken);
}

public static class PairingStateMachine
{
    public static PairingAttempt Offer(PairingAttemptId attemptId, DateTimeOffset nowUtc) =>
        new(
            attemptId,
            PairingAttemptStage.Offered,
            ProtocolGuard.Utc(nowUtc, nameof(nowUtc)),
            nowUtc.Add(ProtocolBounds.PairingLifetime));

    /// <summary>
    /// Binds the first request after the transport has matched and consumed the rate-limited
    /// one-time code. A second request cannot replace the bound key.
    /// </summary>
    public static PairingAttempt BindResolvedCode(
        PairingAttempt attempt,
        PairingRequest request,
        DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.Offered, nowUtc);
        if (request.AttemptId != attempt.AttemptId)
        {
            throw new ArgumentException("The request names another attempt.", nameof(request));
        }

        return new PairingAttempt(
            attempt.AttemptId,
            PairingAttemptStage.AwaitingDesktopApproval,
            attempt.OfferedUtc,
            attempt.ExpiresUtc,
            nowUtc,
            request);
    }

    public static PairingAttempt Approve(
        PairingAttempt attempt,
        PairingChallenge challenge,
        DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        if (challenge.AttemptId != attempt.AttemptId ||
            challenge.DeviceKeyId != attempt.Request!.DeviceKey.KeyId ||
            challenge.ApprovedUtc != nowUtc ||
            challenge.ExpiresUtc > attempt.ExpiresUtc)
        {
            throw new ArgumentException("The approval challenge is not bound to this request and instant.", nameof(challenge));
        }

        return new PairingAttempt(
            attempt.AttemptId,
            PairingAttemptStage.AwaitingDeviceProof,
            attempt.OfferedUtc,
            attempt.ExpiresUtc,
            attempt.CodeConsumedUtc,
            attempt.Request,
            challenge);
    }

    public static PairingAttempt Deny(PairingAttempt attempt, DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        return new PairingAttempt(
            attempt.AttemptId,
            PairingAttemptStage.Denied,
            attempt.OfferedUtc,
            attempt.ExpiresUtc,
            attempt.CodeConsumedUtc,
            attempt.Request,
            endedUtc: nowUtc);
    }

    public static async ValueTask<PairingAttempt> CompleteAsync(
        PairingAttempt attempt,
        PairingProof proof,
        IDeviceKeyProofVerifier verifier,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDeviceProof, nowUtc);
        ArgumentNullException.ThrowIfNull(verifier);
        if (nowUtc >= attempt.Challenge!.ExpiresUtc)
        {
            throw new InvalidOperationException("The approved device proof challenge has expired.");
        }

        if (!string.Equals(proof.ChallengeId, attempt.Challenge!.ChallengeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The proof names another challenge.");
        }

        if (!await verifier.VerifyAsync(
                attempt.Request!.DeviceKey,
                attempt.Challenge,
                proof,
                cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The bound device key did not prove the approved challenge.");
        }

        return new PairingAttempt(
            attempt.AttemptId,
            PairingAttemptStage.Completed,
            attempt.OfferedUtc,
            attempt.ExpiresUtc,
            attempt.CodeConsumedUtc,
            attempt.Request,
            attempt.Challenge,
            nowUtc);
    }

    public static PairingAttempt Expire(PairingAttempt attempt, DateTimeOffset nowUtc)
    {
        ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (nowUtc < attempt.ExpiresUtc)
        {
            throw new InvalidOperationException("A live pairing attempt cannot expire early.");
        }

        if (attempt.Stage is PairingAttemptStage.Completed or PairingAttemptStage.Denied or PairingAttemptStage.Expired)
        {
            throw new InvalidOperationException("A terminal pairing attempt cannot transition again.");
        }

        return new PairingAttempt(
            attempt.AttemptId,
            PairingAttemptStage.Expired,
            attempt.OfferedUtc,
            attempt.ExpiresUtc,
            attempt.CodeConsumedUtc,
            attempt.Request,
            attempt.Challenge,
            attempt.ExpiresUtc);
    }

    private static void RequireLiveStage(
        PairingAttempt attempt,
        PairingAttemptStage required,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (attempt.Stage != required)
        {
            throw new InvalidOperationException($"Expected pairing stage {required}, not {attempt.Stage}.");
        }

        if (nowUtc >= attempt.ExpiresUtc)
        {
            throw new InvalidOperationException("The pairing attempt has expired.");
        }
    }
}

public sealed record PairingRateObservation(string SourceHash, DateTimeOffset ObservedUtc)
{
    public string SourceHash { get; } = ProtocolGuard.Base64Url(
        SourceHash,
        nameof(SourceHash),
        ProtocolBounds.MaxShortStringBytes);

    public DateTimeOffset ObservedUtc { get; } = ProtocolGuard.Utc(ObservedUtc, nameof(ObservedUtc));
}

public sealed record PairingRateState
{
    public PairingRateState(IReadOnlyList<PairingRateObservation> observations)
    {
        Observations = ProtocolGuard.List(
            observations,
            nameof(observations),
            ProtocolBounds.MaxDevices * ProtocolBounds.MaxPairingAttemptsPerWindow);
    }

    public IReadOnlyList<PairingRateObservation> Observations { get; }

    public static PairingRateState Empty { get; } = new([]);
}

public sealed record PairingRateDecision(bool Accepted, DateTimeOffset? RetryAfterUtc, PairingRateState State);

public static class PairingRateLimiter
{
    public static PairingRateDecision TryConsume(
        PairingRateState state,
        string sourceHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var source = ProtocolGuard.Base64Url(sourceHash, nameof(sourceHash), ProtocolBounds.MaxShortStringBytes);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        var cutoff = now.Subtract(ProtocolBounds.PairingRateWindow);
        var live = state.Observations.Where(item => item.ObservedUtc > cutoff).ToList();
        var matching = live.Where(item => string.Equals(item.SourceHash, source, StringComparison.Ordinal)).ToArray();
        if (matching.Length >= ProtocolBounds.MaxPairingAttemptsPerWindow)
        {
            return new PairingRateDecision(
                false,
                matching.Min(item => item.ObservedUtc).Add(ProtocolBounds.PairingRateWindow),
                new PairingRateState(live));
        }

        live.Add(new PairingRateObservation(source, now));
        return new PairingRateDecision(true, null, new PairingRateState(live));
    }
}
