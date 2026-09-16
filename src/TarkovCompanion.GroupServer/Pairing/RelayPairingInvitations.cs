using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.GroupServer.Pairing;

public sealed record PairingInvitationCredential(
    PairingAttemptId AttemptId,
    string ShortCode,
    DateTimeOffset ExpiresUtc);

public sealed record PairingInvitationView(
    PairingAttemptId AttemptId,
    PairingAttemptStage Stage,
    DateTimeOffset OfferedUtc,
    DateTimeOffset ExpiresUtc,
    string? RequestedDeviceName,
    DeviceKeyId? RequestedDeviceKeyId);

public sealed record RelayPairingResult<T>(bool Succeeded, string Code, T? Value, DateTimeOffset? RetryAfterUtc = null)
{
    public static RelayPairingResult<T> Success(T value) => new(true, "accepted", value);

    public static RelayPairingResult<T> Reject(string code, DateTimeOffset? retryAfterUtc = null) =>
        new(false, code, default, retryAfterUtc);
}

/// <summary>Holds short-lived, owner-created, single-use #276 pairing invitations.</summary>
/// <remarks>
/// The human code is only a lookup value: the service stores its digest, consumes it on the first
/// resolved request, then still requires desktop-owner approval and proof of the bound WebAuthn
/// key. The code is never accepted as a device session or room credential.
/// </remarks>
public sealed class RelayPairingInvitations
{
    private const int CodeBytes = 10;
    private readonly TimeProvider _timeProvider;
    private readonly RelayRateLimiter _resolveRate;
    private readonly Lock _gate = new();
    private readonly Dictionary<PairingAttemptId, Invitation> _invitations = [];

    public RelayPairingInvitations(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _resolveRate = new RelayRateLimiter(
            timeProvider,
            ProtocolBounds.MaxPairingAttemptsPerWindow,
            ProtocolBounds.PairingRateWindow);
    }

    public RelayPairingResult<PairingInvitationCredential> Create(RelayPrincipal owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!RelayAuthorization.Decide(owner, RelayPermission.CreatePairingInvitation).Allowed)
        {
            return RelayPairingResult<PairingInvitationCredential>.Reject("not-authorized");
        }

        lock (_gate)
        {
            var now = Now();
            SweepCore(now);
            if (owner.ExpiresUtc <= now || _invitations.Count >= RelaySecurityBounds.MaximumPairingInvitations)
            {
                return RelayPairingResult<PairingInvitationCredential>.Reject("pairing-unavailable");
            }

            var attemptId = new PairingAttemptId(Guid.NewGuid());
            var attempt = PairingStateMachine.Offer(attemptId, now);
            var code = RelayCsrfProtector.Base64Url(RandomNumberGenerator.GetBytes(CodeBytes));
            _invitations.Add(attemptId, new Invitation(
                owner.DeviceId,
                owner.ChannelId,
                Digest(code),
                attempt));
            return RelayPairingResult<PairingInvitationCredential>.Success(
                new PairingInvitationCredential(attemptId, code, attempt.ExpiresUtc));
        }
    }

    public RelayPairingResult<PairingInvitationView> Resolve(
        string shortCode,
        string sourceHash,
        PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rate = _resolveRate.TryConsume(sourceHash);
        if (!rate.Allowed)
        {
            return RelayPairingResult<PairingInvitationView>.Reject("rate-limited", rate.RetryAfterUtc);
        }

        if (string.IsNullOrWhiteSpace(shortCode) || Encoding.UTF8.GetByteCount(shortCode) > 64)
        {
            return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
        }

        var presented = Digest(shortCode);
        lock (_gate)
        {
            var now = Now();
            SweepCore(now);
            Invitation? match = null;
            foreach (var candidate in _invitations.Values)
            {
                if (CryptographicOperations.FixedTimeEquals(candidate.CodeDigest, presented) &&
                    candidate.Attempt.Stage == PairingAttemptStage.Offered)
                {
                    match = candidate;
                }
            }

            if (match is null || match.Attempt.AttemptId != request.AttemptId)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }

            try
            {
                var bound = PairingStateMachine.BindResolvedCode(match.Attempt, request, now);
                _invitations[bound.AttemptId] = match with { Attempt = bound, CodeDigest = ZeroDigest() };
                return RelayPairingResult<PairingInvitationView>.Success(View(bound));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }
        }
    }

    public RelayPairingResult<PairingInvitationView> Approve(
        RelayPrincipal owner,
        PairingChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(challenge);
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation).Allowed)
        {
            return RelayPairingResult<PairingInvitationView>.Reject("not-authorized");
        }

        lock (_gate)
        {
            var now = Now();
            SweepCore(now);
            if (!_invitations.TryGetValue(challenge.AttemptId, out var invitation) ||
                invitation.OwnerDeviceId != owner.DeviceId || invitation.ChannelId != owner.ChannelId ||
                owner.ExpiresUtc <= now)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }

            try
            {
                var approved = PairingStateMachine.Approve(invitation.Attempt, challenge, now);
                _invitations[approved.AttemptId] = invitation with { Attempt = approved };
                return RelayPairingResult<PairingInvitationView>.Success(View(approved));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }
        }
    }

    public RelayPairingResult<PairingInvitationView> Deny(RelayPrincipal owner, PairingAttemptId attemptId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation).Allowed)
        {
            return RelayPairingResult<PairingInvitationView>.Reject("not-authorized");
        }

        lock (_gate)
        {
            var now = Now();
            SweepCore(now);
            if (!_invitations.TryGetValue(attemptId, out var invitation) ||
                invitation.OwnerDeviceId != owner.DeviceId || invitation.ChannelId != owner.ChannelId)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }

            try
            {
                var denied = PairingStateMachine.Deny(invitation.Attempt, now);
                _invitations[attemptId] = invitation with { Attempt = denied, CodeDigest = ZeroDigest() };
                return RelayPairingResult<PairingInvitationView>.Success(View(denied));
            }
            catch (InvalidOperationException)
            {
                return RelayPairingResult<PairingInvitationView>.Reject("pairing-rejected");
            }
        }
    }

    public async ValueTask<RelayPairingResult<PairingAttempt>> CompleteAsync(
        PairingAttemptId attemptId,
        PairingProof proof,
        IDeviceKeyProofVerifier verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(verifier);
        Invitation invitation;
        lock (_gate)
        {
            var now = Now();
            SweepCore(now);
            if (!_invitations.TryGetValue(attemptId, out var found) ||
                found.Attempt.Stage != PairingAttemptStage.AwaitingDeviceProof)
            {
                return RelayPairingResult<PairingAttempt>.Reject("pairing-rejected");
            }

            invitation = found;
        }

        PairingAttempt completed;
        try
        {
            completed = await PairingStateMachine.CompleteAsync(
                invitation.Attempt,
                proof,
                verifier,
                Now(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return RelayPairingResult<PairingAttempt>.Reject("pairing-rejected");
        }

        lock (_gate)
        {
            if (!_invitations.TryGetValue(attemptId, out var current) || current != invitation)
            {
                return RelayPairingResult<PairingAttempt>.Reject("pairing-rejected");
            }

            _invitations[attemptId] = current with { Attempt = completed, CodeDigest = ZeroDigest() };
            return RelayPairingResult<PairingAttempt>.Success(completed);
        }
    }

    public IReadOnlyList<PairingInvitationView> PendingFor(RelayPrincipal owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation).Allowed)
        {
            return [];
        }

        lock (_gate)
        {
            SweepCore(Now());
            return _invitations.Values
                .Where(invitation => invitation.OwnerDeviceId == owner.DeviceId &&
                                     invitation.ChannelId == owner.ChannelId &&
                                     invitation.Attempt.Stage is PairingAttemptStage.AwaitingDesktopApproval or
                                         PairingAttemptStage.AwaitingDeviceProof)
                .Select(invitation => View(invitation.Attempt))
                .OrderBy(view => view.OfferedUtc)
                .ToArray();
        }
    }

    public int Sweep()
    {
        lock (_gate)
        {
            return SweepCore(Now());
        }
    }

    private int SweepCore(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (id, invitation) in _invitations.ToArray())
        {
            var attempt = invitation.Attempt;
            if (attempt.Stage is PairingAttemptStage.Completed or PairingAttemptStage.Denied or PairingAttemptStage.Expired)
            {
                if (now >= attempt.ExpiresUtc && _invitations.Remove(id))
                {
                    removed++;
                }

                continue;
            }

            if (now >= attempt.ExpiresUtc)
            {
                _invitations[id] = invitation with
                {
                    Attempt = PairingStateMachine.Expire(attempt, now),
                    CodeDigest = ZeroDigest(),
                };
            }
        }

        return removed;
    }

    private static PairingInvitationView View(PairingAttempt attempt) => new(
        attempt.AttemptId,
        attempt.Stage,
        attempt.OfferedUtc,
        attempt.ExpiresUtc,
        attempt.Request?.RequestedDeviceName,
        attempt.Request?.DeviceKey.KeyId);

    private static byte[] Digest(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static byte[] ZeroDigest() => new byte[SHA256.HashSizeInBytes];

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private sealed record Invitation(
        CompanionDeviceId OwnerDeviceId,
        RelayChannelId ChannelId,
        byte[] CodeDigest,
        PairingAttempt Attempt);
}
