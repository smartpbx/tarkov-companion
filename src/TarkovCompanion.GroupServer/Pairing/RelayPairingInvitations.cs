using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.GroupServer.Pairing;

public sealed record PairingInvitationCredential(
    PairingAttemptId AttemptId,
    string ShortCode,
    DateTimeOffset ExpiresUtc);

public sealed record PairingInvitationView(
    PairingAttemptId AttemptId,
    DateTimeOffset OfferedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? CodeConsumedUtc,
    DateTimeOffset? RevokedUtc);

public sealed record RelayPairingRoute(
    PairingAttemptId AttemptId,
    CompanionDeviceId OwnerDeviceId,
    DeviceSessionId OwnerSessionId,
    DateTimeOffset ExpiresUtc);

public sealed record RelayPairingResult<T>(bool Succeeded, string Code, T? Value, DateTimeOffset? RetryAfterUtc = null)
{
    public static RelayPairingResult<T> Success(T value) => new(true, "accepted", value);

    public static RelayPairingResult<T> Reject(string code, DateTimeOffset? retryAfterUtc = null) =>
        new(false, code, default, retryAfterUtc);
}

/// <summary>Bounded, one-use lookup and routing state for desktop-created pairing offers.</summary>
/// <remarks>
/// The relay is not a handshake authority. It never holds the desktop nonce, a device name,
/// challenge, proof, or session secret and cannot advance <see cref="PairingStateMachine"/>. It
/// rate-limits and consumes the short code, publishes the already-created public offer, and keeps
/// only enough owner identity to route the remaining opaque handshake connection.
/// </remarks>
public sealed class RelayPairingInvitations : IDisposable
{
    private readonly RelayDeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _codeDigestKey = RandomNumberGenerator.GetBytes(32);
    private readonly Lock _gate = new();
    private readonly Dictionary<PairingAttemptId, Invitation> _invitations = [];
    private PairingRateState _rateState = PairingRateState.Empty;
    private bool _disposed;

    public RelayPairingInvitations(RelayDeviceRegistry registry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _timeProvider = timeProvider;
    }

    public RelayPairingResult<PairingInvitationCredential> Register(
        RelayPrincipal owner,
        PairingOffer offer,
        string pairingCode)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(offer);
        var now = Now();
        if (!RelayAuthorization.Decide(owner, RelayPermission.CreatePairingInvitation, now).Allowed ||
            !_registry.IsCurrent(owner))
        {
            return RelayPairingResult<PairingInvitationCredential>.Reject("not-authorized");
        }

        string normalized;
        try
        {
            normalized = PairedTransportBinding.NormalizePairingCode(pairingCode);
        }
        catch (ArgumentException)
        {
            return RelayPairingResult<PairingInvitationCredential>.Reject("pairing-rejected");
        }

        if (offer.OfferedUtc > now.Add(ProtocolBounds.MaxClientClockSkew) || offer.ExpiresUtc <= now)
        {
            return RelayPairingResult<PairingInvitationCredential>.Reject("pairing-rejected");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            SweepCore(now);
            if (_invitations.Count >= RelaySecurityBounds.MaximumPairingInvitations)
            {
                return RelayPairingResult<PairingInvitationCredential>.Reject("invitation-limit");
            }

            var digest = Digest(normalized);
            if (_invitations.ContainsKey(offer.AttemptId) ||
                _invitations.Values.Any(item => item.CodeDigest.Length != 0 &&
                    CryptographicOperations.FixedTimeEquals(item.CodeDigest, digest)))
            {
                CryptographicOperations.ZeroMemory(digest);
                return RelayPairingResult<PairingInvitationCredential>.Reject("pairing-rejected");
            }

            _invitations.Add(
                offer.AttemptId,
                new Invitation(owner.DeviceId, owner.SessionId, digest, offer, null, null));
            return RelayPairingResult<PairingInvitationCredential>.Success(
                new PairingInvitationCredential(offer.AttemptId, normalized, offer.ExpiresUtc));
        }
    }

    /// <summary>Consumes a rate-limited code before revealing its public pairing offer.</summary>
    public RelayPairingResult<PairingOffer> Resolve(string? pairingCode, string sourceHash)
    {
        var now = Now();
        lock (_gate)
        {
            ThrowIfDisposed();
            SweepCore(now);
            PairingRateDecision rate;
            try
            {
                rate = PairingRateLimiter.TryConsume(_rateState, sourceHash, now);
            }
            catch (ArgumentException)
            {
                return RelayPairingResult<PairingOffer>.Reject("pairing-rejected");
            }

            _rateState = rate.State;
            if (!rate.Accepted)
            {
                return RelayPairingResult<PairingOffer>.Reject("rate-limited", rate.RetryAfterUtc);
            }

            string normalized;
            try
            {
                normalized = PairedTransportBinding.NormalizePairingCode(pairingCode);
            }
            catch (ArgumentException)
            {
                return RelayPairingResult<PairingOffer>.Reject("pairing-rejected");
            }

            var presented = Digest(normalized);
            try
            {
                PairingAttemptId matchedAttemptId = default;
                Invitation? matched = null;
                foreach (var (attemptId, invitation) in _invitations)
                {
                    if (invitation.CodeConsumedUtc is not null || invitation.RevokedUtc is not null ||
                        invitation.CodeDigest.Length == 0 ||
                        !CryptographicOperations.FixedTimeEquals(invitation.CodeDigest, presented))
                    {
                        continue;
                    }

                    matchedAttemptId = attemptId;
                    matched = invitation;
                    break;
                }

                if (matched is not null)
                {
                    _invitations[matchedAttemptId] = matched with { CodeConsumedUtc = now };
                    return RelayPairingResult<PairingOffer>.Success(matched.Offer);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(presented);
            }

            return RelayPairingResult<PairingOffer>.Reject("pairing-rejected");
        }
    }

    public RelayPairingResult<bool> Revoke(RelayPrincipal owner, PairingAttemptId attemptId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var now = Now();
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation, now).Allowed ||
            !_registry.IsCurrent(owner))
        {
            return RelayPairingResult<bool>.Reject("not-authorized");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            SweepCore(now);
            if (!_invitations.TryGetValue(attemptId, out var invitation) ||
                invitation.OwnerDeviceId != owner.DeviceId || invitation.RevokedUtc is not null)
            {
                return RelayPairingResult<bool>.Reject("pairing-rejected");
            }

            _invitations[attemptId] = invitation with { RevokedUtc = now };
            return RelayPairingResult<bool>.Success(true);
        }
    }

    public RelayPairingResult<RelayPairingRoute> RouteFor(PairingAttemptId attemptId)
    {
        Invitation invitation;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = Now();
            SweepCore(now);
            if (!_invitations.TryGetValue(attemptId, out invitation) ||
                invitation.CodeConsumedUtc is null || invitation.RevokedUtc is not null)
            {
                return RelayPairingResult<RelayPairingRoute>.Reject("pairing-rejected");
            }
        }

        var route = _registry.ActiveRoute(invitation.OwnerSessionId);
        return route is not null && route.DeviceId == invitation.OwnerDeviceId
            ? RelayPairingResult<RelayPairingRoute>.Success(new RelayPairingRoute(
                invitation.Offer.AttemptId,
                invitation.OwnerDeviceId,
                invitation.OwnerSessionId,
                invitation.Offer.ExpiresUtc))
            : RelayPairingResult<RelayPairingRoute>.Reject("pairing-rejected");
    }

    public IReadOnlyList<PairingInvitationView> PendingFor(RelayPrincipal owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var now = Now();
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation, now).Allowed ||
            !_registry.IsCurrent(owner))
        {
            return [];
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            SweepCore(now);
            return _invitations.Values
                .Where(invitation => invitation.OwnerDeviceId == owner.DeviceId && invitation.RevokedUtc is null)
                .Select(View)
                .OrderBy(view => view.OfferedUtc)
                .ToArray();
        }
    }

    public int Sweep()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return SweepCore(Now());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var invitation in _invitations.Values)
            {
                CryptographicOperations.ZeroMemory(invitation.CodeDigest);
            }

            _invitations.Clear();
            CryptographicOperations.ZeroMemory(_codeDigestKey);
            _disposed = true;
        }
    }

    private int SweepCore(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (attemptId, invitation) in _invitations.ToArray())
        {
            if (now < invitation.Offer.ExpiresUtc)
            {
                continue;
            }

            CryptographicOperations.ZeroMemory(invitation.CodeDigest);
            _invitations.Remove(attemptId);
            removed++;
        }

        return removed;
    }

    private byte[] Digest(string normalizedCode) =>
        HMACSHA256.HashData(_codeDigestKey, Encoding.ASCII.GetBytes(normalizedCode));

    private static PairingInvitationView View(Invitation invitation) => new(
        invitation.Offer.AttemptId,
        invitation.Offer.OfferedUtc,
        invitation.Offer.ExpiresUtc,
        invitation.CodeConsumedUtc,
        invitation.RevokedUtc);

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record Invitation(
        CompanionDeviceId OwnerDeviceId,
        DeviceSessionId OwnerSessionId,
        byte[] CodeDigest,
        PairingOffer Offer,
        DateTimeOffset? CodeConsumedUtc,
        DateTimeOffset? RevokedUtc);
}
