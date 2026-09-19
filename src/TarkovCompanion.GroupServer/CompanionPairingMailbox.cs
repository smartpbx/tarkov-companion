using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer;

public sealed record MailboxResult<T>(bool Succeeded, string Code, T? Value, DateTimeOffset? RetryAfterUtc = null)
{
    public static MailboxResult<T> Success(T value) => new(true, "accepted", value);

    public static MailboxResult<T> Reject(string code, DateTimeOffset? retryAfterUtc = null) =>
        new(false, code, default, retryAfterUtc);
}

/// <summary>
/// A bounded, expiring relay of the paired-device pairing handshake's plaintext wire roots, keyed
/// by pairing attempt.
/// </summary>
/// <remarks>
/// This is the rough-pass companion to the "Transport binding" section of
/// docs/PAIRED_DEVICE_PROTOCOL.md: it moves <see cref="PairingOffer"/>, <see cref="PairingRequest"/>,
/// <see cref="HandshakeChallenge"/>, <see cref="DeviceKeyProof"/> and <see cref="SessionEstablished"/>
/// between a desktop and a tablet that cannot reach each other directly. It never advances
/// <see cref="PairingStateMachine"/> and never sees the desktop nonce, a device name, or any traffic
/// key — the desktop and tablet run the whole ceremony themselves and only pass its public messages
/// through here, the same way <c>RelayPairingInvitations</c> describes a relay's role. It does not
/// carry the established session's opaque frames; see the package PR for why that is deferred.
/// </remarks>
public sealed class CompanionPairingMailbox
{
    private const int MaximumAttempts = 64;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _codeDigestKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _sourceHashKey = RandomNumberGenerator.GetBytes(32);
    private readonly Lock _gate = new();
    private readonly Dictionary<PairingAttemptId, Entry> _entries = [];
    private PairingRateState _rateState = PairingRateState.Empty;
    private PairingRateState _registerRateState = PairingRateState.Empty;

    public CompanionPairingMailbox(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Registers a desktop-created offer, bounded the same way a resolve is.</summary>
    /// <remarks>
    /// <see cref="PairingOffer"/> only bounds <c>ExpiresUtc - OfferedUtc</c> to at most ten
    /// minutes; <c>OfferedUtc</c> itself is whatever the caller wrote into the JSON body. Without
    /// a clock check, a future-dated offer's expiry is future-dated too, so <see cref="Sweep"/>
    /// never reclaims it: 64 such offers fill the table and every real pairing on this relay sees
    /// "invitation-limit" until the process restarts. The same per-source rate limit
    /// <see cref="ResolveOffer"/> already applies to code lookups applies here to registrations.
    /// </remarks>
    public MailboxResult<bool> RegisterOffer(PairingOffer offer, string? pairingCode, string remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(offer);
        // [V2 rough package 48] Named causes. Every refusal here used to be "pairing-rejected",
        // so six identical 400s in a relay journal said nothing about which of five things was
        // wrong, and the desktop could only ever offer one guess. None of these tells a caller
        // anything it did not itself send.
        var now = Now();
        if (offer.OfferedUtc > now.Add(ProtocolBounds.MaxClientClockSkew))
        {
            return MailboxResult<bool>.Reject("offer-future-dated");
        }

        if (offer.ExpiresUtc <= now)
        {
            return MailboxResult<bool>.Reject("offer-expired");
        }

        string normalized;
        try
        {
            normalized = PairedTransportBinding.NormalizePairingCode(pairingCode);
        }
        catch (ArgumentException)
        {
            return MailboxResult<bool>.Reject("pairing-code-malformed");
        }

        lock (_gate)
        {
            Sweep(now);
            var sourceHash = Base64Url.EncodeToString(Digest(_sourceHashKey, remoteAddress));
            PairingRateDecision rate;
            try
            {
                rate = PairingRateLimiter.TryConsume(_registerRateState, sourceHash, now);
            }
            catch (ArgumentException)
            {
                return MailboxResult<bool>.Reject("pairing-rejected");
            }

            _registerRateState = rate.State;
            if (!rate.Accepted)
            {
                return MailboxResult<bool>.Reject("rate-limited", rate.RetryAfterUtc);
            }

            if (_entries.ContainsKey(offer.AttemptId))
            {
                return MailboxResult<bool>.Reject("attempt-duplicate");
            }

            if (_entries.Count >= MaximumAttempts)
            {
                return MailboxResult<bool>.Reject("invitation-limit");
            }

            _entries.Add(offer.AttemptId, new Entry(offer, Digest(_codeDigestKey, normalized)));
            return MailboxResult<bool>.Success(true);
        }
    }

    /// <summary>Consumes a rate-limited code before revealing its public pairing offer.</summary>
    public MailboxResult<PairingOffer> ResolveOffer(string? pairingCode, string remoteAddress)
    {
        var now = Now();
        string normalized;
        try
        {
            normalized = PairedTransportBinding.NormalizePairingCode(pairingCode);
        }
        catch (ArgumentException)
        {
            return MailboxResult<PairingOffer>.Reject("pairing-rejected");
        }

        lock (_gate)
        {
            Sweep(now);
            var sourceHash = Base64Url.EncodeToString(Digest(_sourceHashKey, remoteAddress));
            PairingRateDecision rate;
            try
            {
                rate = PairingRateLimiter.TryConsume(_rateState, sourceHash, now);
            }
            catch (ArgumentException)
            {
                return MailboxResult<PairingOffer>.Reject("pairing-rejected");
            }

            _rateState = rate.State;
            if (!rate.Accepted)
            {
                return MailboxResult<PairingOffer>.Reject("rate-limited", rate.RetryAfterUtc);
            }

            var presented = Digest(_codeDigestKey, normalized);
            foreach (var (attemptId, entry) in _entries)
            {
                if (entry.CodeConsumedUtc is not null ||
                    !CryptographicOperations.FixedTimeEquals(entry.CodeDigest, presented))
                {
                    continue;
                }

                _entries[attemptId] = entry with { CodeConsumedUtc = now };
                return MailboxResult<PairingOffer>.Success(entry.Offer);
            }

            return MailboxResult<PairingOffer>.Reject("pairing-rejected");
        }
    }

    public MailboxResult<bool> SubmitRequest(PairingAttemptId attemptId, PairingRequest request) =>
        SetOnce(attemptId, request, entry => entry.Request, (entry, value) => entry with { Request = value });

    public PairingRequest? ReadRequest(PairingAttemptId attemptId) => Get(attemptId)?.Request;

    public MailboxResult<bool> SubmitNonceReveal(PairingAttemptId attemptId, PairingNonceReveal reveal) =>
        SetOnce(attemptId, reveal, entry => entry.NonceReveal, (entry, value) => entry with { NonceReveal = value });

    public PairingNonceReveal? ReadNonceReveal(PairingAttemptId attemptId) => Get(attemptId)?.NonceReveal;

    public MailboxResult<bool> SubmitChallenge(PairingAttemptId attemptId, HandshakeChallenge challenge) =>
        SetOnce(attemptId, challenge, entry => entry.Challenge, (entry, value) => entry with { Challenge = value });

    public HandshakeChallenge? ReadChallenge(PairingAttemptId attemptId) => Get(attemptId)?.Challenge;

    public MailboxResult<bool> SubmitProof(PairingAttemptId attemptId, DeviceKeyProof proof) =>
        SetOnce(attemptId, proof, entry => entry.Proof, (entry, value) => entry with { Proof = value });

    public DeviceKeyProof? ReadProof(PairingAttemptId attemptId) => Get(attemptId)?.Proof;

    public MailboxResult<bool> SubmitEstablished(PairingAttemptId attemptId, SessionEstablished established) =>
        SetOnce(
            attemptId,
            established,
            entry => entry.Established,
            (entry, value) => entry with { Established = value });

    public SessionEstablished? ReadEstablished(PairingAttemptId attemptId) => Get(attemptId)?.Established;

    /// <summary>
    /// The desktop hands the tablet's own relay bearer secret through once it has registered the
    /// device on the relay (v2r-relay-owner's <c>POST /v2/companion/relay/devices</c>).
    /// <paramref name="relaySession"/> is <see cref="RelayCredentialCryptography.Seal"/>'s output
    /// (sealed with the pairing's own desktop-to-tablet traffic key, ABUSE-PAIRED-LIVE-BEARER-THEFT)
    /// — this mailbox never sees, stores, or forwards the plaintext bearer secret. First write wins
    /// — every other slot here allows an idempotent resubmission of the identical value, but
    /// <see cref="RelayCredentialCryptography.Seal"/> reseals with a fresh random nonce every call,
    /// so a retried registration never produces byte-identical ciphertext even for the same secret;
    /// accepting "the same value" here would have to mean "decrypts to the same plaintext", and
    /// this mailbox deliberately never decrypts anything. <see cref="ReadRelaySession"/> additionally
    /// consumes it on its first successful read, bounded the same way every other slot expires with
    /// the offer.
    /// </summary>
    public MailboxResult<bool> SubmitRelaySession(PairingAttemptId attemptId, SealedRelayCredential relaySession)
    {
        ArgumentNullException.ThrowIfNull(relaySession);
        var now = Now();
        lock (_gate)
        {
            Sweep(now);
            if (!_entries.TryGetValue(attemptId, out var entry) || entry.Offer.ExpiresUtc <= now)
            {
                return MailboxResult<bool>.Reject("pairing-rejected");
            }

            if (entry.RelaySessionSubmitted)
            {
                return MailboxResult<bool>.Reject("pairing-rejected");
            }

            _entries[attemptId] = entry with { RelaySession = relaySession, RelaySessionSubmitted = true };
            return MailboxResult<bool>.Success(true);
        }
    }

    /// <summary>Consumes the sealed relay session credential: null after the first successful read, the same as a miss.</summary>
    public SealedRelayCredential? ReadRelaySession(PairingAttemptId attemptId)
    {
        var now = Now();
        lock (_gate)
        {
            Sweep(now);
            if (!_entries.TryGetValue(attemptId, out var entry) || entry.RelaySession is not { } relaySession)
            {
                return null;
            }

            _entries[attemptId] = entry with { RelaySession = null };
            return relaySession;
        }
    }

    /// <summary>Lets the desktop tell a waiting tablet its request was declined.</summary>
    public MailboxResult<bool> Deny(PairingAttemptId attemptId)
    {
        lock (_gate)
        {
            Sweep(Now());
            if (!_entries.TryGetValue(attemptId, out var entry))
            {
                return MailboxResult<bool>.Reject("pairing-rejected");
            }

            _entries[attemptId] = entry with { Denied = true };
            return MailboxResult<bool>.Success(true);
        }
    }

    public bool IsDenied(PairingAttemptId attemptId) => Get(attemptId)?.Denied == true;

    public int Sweep()
    {
        lock (_gate)
        {
            return Sweep(Now());
        }
    }

    private MailboxResult<bool> SetOnce<T>(
        PairingAttemptId attemptId,
        T value,
        Func<Entry, T?> getter,
        Func<Entry, T, Entry> wither)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var now = Now();
        lock (_gate)
        {
            Sweep(now);
            if (!_entries.TryGetValue(attemptId, out var entry) || entry.Offer.ExpiresUtc <= now)
            {
                return MailboxResult<bool>.Reject("pairing-rejected");
            }

            if (getter(entry) is { } existing)
            {
                // A transport retry resends the identical message; anything else would let a
                // second sender overwrite what the other side already polled for.
                var same = CompanionProtocolJson.Serialize(existing).AsSpan()
                    .SequenceEqual(CompanionProtocolJson.Serialize(value));
                return same ? MailboxResult<bool>.Success(true) : MailboxResult<bool>.Reject("pairing-rejected");
            }

            _entries[attemptId] = wither(entry, value);
            return MailboxResult<bool>.Success(true);
        }
    }

    private Entry? Get(PairingAttemptId attemptId)
    {
        lock (_gate)
        {
            Sweep(Now());
            return _entries.TryGetValue(attemptId, out var entry) ? entry : null;
        }
    }

    private int Sweep(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (attemptId, entry) in _entries.ToArray())
        {
            // A minute past the offer's own expiry, so a party's last poll for Established still
            // finds it even when it lands right at the boundary.
            if (now < entry.Offer.ExpiresUtc.AddMinutes(1))
            {
                continue;
            }

            _entries.Remove(attemptId);
            removed++;
        }

        return removed;
    }

    private static byte[] Digest(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private sealed record Entry(
        PairingOffer Offer,
        byte[] CodeDigest,
        DateTimeOffset? CodeConsumedUtc = null,
        PairingRequest? Request = null,
        PairingNonceReveal? NonceReveal = null,
        HandshakeChallenge? Challenge = null,
        DeviceKeyProof? Proof = null,
        SessionEstablished? Established = null,
        SealedRelayCredential? RelaySession = null,
        bool RelaySessionSubmitted = false,
        bool Denied = false);
}
