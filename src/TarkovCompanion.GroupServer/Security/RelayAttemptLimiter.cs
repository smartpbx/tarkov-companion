using System.Collections.Concurrent;
using System.Net;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>
/// How much a caller is slowed down for getting a key wrong.
/// </summary>
/// <remarks>
/// [#317, RISK-RELAY-KEY-BRUTEFORCE and RISK-ADMIN-KEY-BRUTEFORCE] Both keys were compared
/// carefully and guessed freely: a group key has a floor of eight characters and an admin key had
/// no floor at all, and nothing anywhere counted a wrong one. A relay on a public name answers as
/// fast as it can be asked, so the only cost of guessing was bandwidth.
///
/// Two controls, deliberately in this order:
///
/// 1. **A delay**, from the fifth failure, doubling to two seconds. This is the one that does the
///    work. It cuts the rate a single address can try keys at by three orders of magnitude while
///    being invisible to somebody who mistyped once.
/// 2. **A refusal**, after twenty failures inside five minutes, for sixty seconds. Twenty wrong
///    keys in five minutes is not a person typing. Sixty seconds rather than the usual longer
///    cooldown because this buckets by remote address, and a relay behind a reverse proxy sees
///    the proxy: a long lockout there would be an outage for everybody, caused by one stranger.
///    A minute costs a legitimate group one retry and still makes bulk guessing pointless.
///
/// A success clears the caller's record, so a group that rejoins after a bad paste is not carrying
/// a penalty into the evening. That is a stated trade: somebody who already holds a key the relay
/// accepts can clear their own penalty between guesses at another. On a closed relay that means an
/// attacker must already be inside it, and on an open one there is nothing to guess, because any
/// key makes a room. The panel page is kept out of the counted paths for the same reason turned
/// the other way — it takes no key and always answers 200, so counting it would hand every caller
/// a reset button. This is not a substitute for the scoped, revocable credentials of
/// #278 and #304; it is the part that can be true of the relay as it is deployed today.
///
/// Separate from <see cref="RelayRateLimiter"/> on purpose. That one admits or refuses requests by
/// volume on the paired-companion routes, whatever their outcome; this one counts the outcome and
/// only on the routes that check a key, because a group polling its own room quickly is not doing
/// anything wrong and getting the key wrong twenty times is.
/// </remarks>
public sealed class RelayAttemptLimiter
{
    public const int FreeAttempts = 4;
    public const int RefusalThreshold = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Refusal = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LongestDelay = TimeSpan.FromSeconds(2);

    /// <summary>How many callers are remembered, so a spray from many addresses cannot grow this.</summary>
    /// <remarks>
    /// Bounded because the dictionary is the one thing here an attacker chooses the size of.
    /// When it is full the oldest record is dropped, which loses that caller's penalty rather
    /// than the relay's memory; refusing to record is the failure that matters least.
    /// </remarks>
    public const int MaximumTracked = 4096;

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Attempts> _callers = new(StringComparer.Ordinal);

    public RelayAttemptLimiter(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>The caller a request is from, as this limiter counts them.</summary>
    /// <remarks>
    /// The transport's own remote address, never a header. `X-Forwarded-For` is written by
    /// whoever is nearest, so counting it would let one caller be as many callers as it liked —
    /// which is the exact thing being limited.
    /// </remarks>
    public static string CallerOf(IPAddress? address) => address?.ToString() ?? "unknown";

    /// <summary>How long this caller waits before their next attempt is answered.</summary>
    public TimeSpan DelayFor(string caller)
    {
        if (!_callers.TryGetValue(caller, out var record) || Expired(record))
        {
            return TimeSpan.Zero;
        }

        var over = record.Failures - FreeAttempts;
        if (over <= 0)
        {
            return TimeSpan.Zero;
        }

        var milliseconds = 100d * Math.Pow(2, Math.Min(over - 1, 10));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, LongestDelay.TotalMilliseconds));
    }

    /// <summary>Whether this caller is refused outright, and until when.</summary>
    public bool IsRefused(string caller, out DateTimeOffset until)
    {
        until = default;
        if (!_callers.TryGetValue(caller, out var record) || Expired(record))
        {
            return false;
        }

        if (record.Failures < RefusalThreshold)
        {
            return false;
        }

        until = record.LastFailureUtc + Refusal;
        return _timeProvider.GetUtcNow() < until;
    }

    /// <summary>Records one answered attempt at a route that checks a key.</summary>
    public void Record(string caller, bool authorised)
    {
        if (authorised)
        {
            _callers.TryRemove(caller, out _);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (_callers.Count >= MaximumTracked / 2)
        {
            Forget(now);
        }
        _callers.AddOrUpdate(
            caller,
            _ => new Attempts(1, now),
            (_, existing) => Expired(existing)
                ? new Attempts(1, now)
                : new Attempts(existing.Failures + 1, now));
    }

    /// <summary>How many callers are currently remembered. For the bound's own test.</summary>
    public int Tracked => _callers.Count;

    private bool Expired(Attempts record) => _timeProvider.GetUtcNow() - record.LastFailureUtc > Window;

    /// <summary>Drops records that can no longer refuse anybody, and the oldest when full.</summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (var (caller, record) in _callers)
        {
            if (now - record.LastFailureUtc > Window)
            {
                _callers.TryRemove(caller, out _);
            }
        }

        while (_callers.Count >= MaximumTracked)
        {
            var oldest = _callers.OrderBy(entry => entry.Value.LastFailureUtc).FirstOrDefault();
            if (oldest.Key is null || !_callers.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }
    }

    private readonly record struct Attempts(int Failures, DateTimeOffset LastFailureUtc);
}
