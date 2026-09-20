using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>One nonce this relay issued for a key holder to sign, and when it stops being good.</summary>
public sealed record RelayPossessionChallenge(Guid ChallengeId, string NonceBase64Url, DateTimeOffset ExpiresUtc);

/// <summary>
/// The relay's half of "prove you hold the key": it hands out a fresh nonce, and the signed answer
/// is good once and only while the nonce is.
/// </summary>
/// <remarks>
/// [#289] A relay session lives twelve hours and a device two idle, so a desktop that was closed
/// overnight came back to a relay that wanted its admin key again, and every tablet paired again.
/// Lengthening those bounds is not the answer (they protect a live owner from being displaced).
/// What the relay can do instead is recognise the same key holder: it already keeps every
/// device's public key durably, and a signature over a nonce only this relay issued cannot be
/// replayed or prepared in advance. Memory-only and bounded: a restart forgets outstanding
/// nonces, which costs a caller one retry.
/// </remarks>
public sealed class RelayPossessionChallenges
{
    public const int MaximumOutstanding = 512;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RelayPossessionChallenge> _outstanding = new(StringComparer.Ordinal);

    public RelayPossessionChallenges(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public RelayPossessionChallenge Issue()
    {
        var now = Now();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
        var issued = new RelayPossessionChallenge(Guid.NewGuid(), nonce, now.Add(ProtocolBounds.HandshakeChallengeLifetime));
        lock (_gate)
        {
            Prune(now);
            if (_outstanding.Count >= MaximumOutstanding)
            {
                // Oldest out: a flood of requests costs an honest caller a retry, never memory.
                _outstanding.Remove(_outstanding.Values.MinBy(item => item.ExpiresUtc)!.NonceBase64Url);
            }

            _outstanding[nonce] = issued;
        }

        return issued;
    }

    /// <summary>The nonce behind a challenge id, exactly once and only before it expires.</summary>
    public string? TryConsume(Guid challengeId)
    {
        var now = Now();
        lock (_gate)
        {
            Prune(now);
            var issued = _outstanding.Values.FirstOrDefault(item => item.ChallengeId == challengeId);
            return issued is not null && _outstanding.Remove(issued.NonceBase64Url) ? issued.NonceBase64Url : null;
        }
    }

    /// <summary>True exactly once per issued nonce, and only before it expires.</summary>
    public bool TryConsume(string? nonceBase64Url)
    {
        if (string.IsNullOrEmpty(nonceBase64Url))
        {
            return false;
        }

        var now = Now();
        lock (_gate)
        {
            Prune(now);
            return _outstanding.Remove(nonceBase64Url);
        }
    }

    /// <summary>
    /// What a paired device signs at the door: never the bare nonce. A device-key assertion over a
    /// bare 32-byte value is exactly what the desktop accepts as a handshake proof, so a relay
    /// that could choose that value could borrow a tablet's signature for a handshake of its own.
    /// A hash under this label cannot be steered onto a transcript hash.
    /// </summary>
    public static string DeviceDoorChallenge(string nonceBase64Url)
    {
        var nonce = Base64Url.DecodeFromChars(nonceBase64Url);
        var label = Encoding.UTF8.GetBytes(DeviceDoorDomain);
        byte[] input = [.. label, .. nonce];
        return Base64Url.EncodeToString(SHA256.HashData(input));
    }

    public const string DeviceDoorDomain = "TarkovCompanion.PairedDevice/v2/relay-resume-door";

    private void Prune(DateTimeOffset now)
    {
        foreach (var expired in _outstanding.Where(pair => now >= pair.Value.ExpiresUtc).Select(pair => pair.Key).ToArray())
        {
            _outstanding.Remove(expired);
        }
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();
}

/// <summary>A paired device that proved its key and is waiting for its desktop to answer.</summary>
public sealed record RelayResumeTicket(Guid TicketId, string DeviceKeyId, DateTimeOffset ExpiresUtc)
{
    /// <summary>The pairing code of the offer the desktop opened for this device, once it has.</summary>
    public string? PairingCode { get; init; }
}

/// <summary>
/// Where a returning tablet and its desktop find each other without a code being typed: the
/// tablet leaves a ticket, the desktop answers it with the code of an offer it just opened.
/// </summary>
/// <remarks>
/// Only reached by a device whose key this relay holds and whose proof it has just checked, so a
/// ticket is never something a stranger can leave. The ticket id is the tablet's alone (128 random
/// bits) and is how it reads the answer. Memory-only and bounded, one ticket per device key.
/// </remarks>
public sealed class RelayResumeTickets
{
    public const int MaximumOutstanding = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RelayResumeTicket> _tickets = [];

    public RelayResumeTickets(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public RelayResumeTicket Open(DeviceKeyId deviceKeyId)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var ticket = new RelayResumeTicket(Guid.NewGuid(), deviceKeyId.Value, now.Add(Lifetime));
        lock (_gate)
        {
            Prune(now);
            foreach (var earlier in _tickets.Values.Where(item => item.DeviceKeyId == ticket.DeviceKeyId).ToArray())
            {
                _tickets.Remove(earlier.TicketId);
            }

            if (_tickets.Count >= MaximumOutstanding)
            {
                _tickets.Remove(_tickets.Values.MinBy(item => item.ExpiresUtc)!.TicketId);
            }

            _tickets[ticket.TicketId] = ticket;
        }

        return ticket;
    }

    /// <summary>Tickets no desktop has answered yet.</summary>
    public IReadOnlyList<RelayResumeTicket> Unanswered()
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        lock (_gate)
        {
            Prune(now);
            return _tickets.Values.Where(item => item.PairingCode is null).ToArray();
        }
    }

    public bool Answer(Guid ticketId, string pairingCode)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        lock (_gate)
        {
            Prune(now);
            if (!_tickets.TryGetValue(ticketId, out var ticket) || ticket.PairingCode is not null)
            {
                return false;
            }

            _tickets[ticketId] = ticket with { PairingCode = pairingCode };
            return true;
        }
    }

    public RelayResumeTicket? Find(Guid ticketId)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        lock (_gate)
        {
            Prune(now);
            return _tickets.GetValueOrDefault(ticketId);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var expired in _tickets.Values.Where(item => now >= item.ExpiresUtc).ToArray())
        {
            _tickets.Remove(expired.TicketId);
        }
    }
}
