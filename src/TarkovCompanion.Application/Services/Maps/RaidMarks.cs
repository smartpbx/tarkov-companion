using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// A local mark's kind, named to match <c>TarkovCompanion.CompanionProtocol.MapMarkKind</c> so a
/// later pairing sync can adapt one to the other without a model rewrite.
/// </summary>
public enum RaidMarkKind
{
    Ping = 1,
    Waypoint,
}

/// <summary>
/// A user-placed mark on a map, kept locally. The payload is the Core v2
/// <see cref="MapMarkState"/> — the same shape the paired protocol's own mark carries — so this
/// store's rows need no translation to become the tablet package's marks.
/// </summary>
public sealed record RaidMark(Guid Id, RaidMarkKind Kind, MapMarkState State, DateTimeOffset CreatedUtc)
{
    /// <summary>Who sees it: this machine (and its paired tablet) only, or the squad too (#289).</summary>
    public RaidMarkScope Scope { get; init; } = RaidMarkScope.Squad;

    /// <summary>How long it lasts, as the player chose it; <see cref="MapMarkState.ExpiresUtc"/> is the resolved instant.</summary>
    public RaidMarkLifetime Lifetime { get; init; } = RaidMarkLifetimes.DefaultFor(Kind);

    /// <summary>The short route this waypoint is a stop on, from a tablet (#290); null for a lone mark.</summary>
    public RaidMarkRoute? Route { get; init; }
}

/// <summary>One stop on a short route: which route, and its place in it from 1.</summary>
public sealed record RaidMarkRoute(Guid RouteId, int Step);

/// <summary>Who a mark is for.</summary>
/// <remarks>
/// A private mark never leaves this machine except to its own paired tablet: the group forwarder
/// skips it. Named for the player's words ("Just me" / "Squad"), not the protocol's three scopes,
/// because the desktop and its tablet are one player.
/// </remarks>
public enum RaidMarkScope
{
    Private = 1,
    Squad,
}

/// <summary>The lifetimes a player can pick for a mark (#289).</summary>
public enum RaidMarkLifetime
{
    /// <summary>A ping: "look here, now", 45 seconds.</summary>
    Ping = 1,

    /// <summary>A waypoint that stays until somebody removes it.</summary>
    UntilRemoved,

    FiveMinutes,

    FifteenMinutes,

    /// <summary>Gone when the raid it was placed in ends.</summary>
    ThisRaid,
}

/// <summary>What each <see cref="RaidMarkLifetime"/> means, in one place for the desktop, the store and the tablet bridge.</summary>
public static class RaidMarkLifetimes
{
    public static IReadOnlyList<RaidMarkLifetime> All { get; } =
        [RaidMarkLifetime.Ping, RaidMarkLifetime.UntilRemoved, RaidMarkLifetime.FiveMinutes, RaidMarkLifetime.FifteenMinutes, RaidMarkLifetime.ThisRaid];

    public static RaidMarkLifetime DefaultFor(RaidMarkKind kind) =>
        kind == RaidMarkKind.Ping ? RaidMarkLifetime.Ping : RaidMarkLifetime.UntilRemoved;

    /// <summary>
    /// Only the 45-second lifetime is a ping. The relay forgets a ping by itself after 45 s, so a
    /// mark meant to last five minutes has to be a waypoint there, and is one here too.
    /// </summary>
    public static RaidMarkKind KindFor(RaidMarkLifetime lifetime) =>
        lifetime == RaidMarkLifetime.Ping ? RaidMarkKind.Ping : RaidMarkKind.Waypoint;

    /// <summary>The instant a mark with this lifetime expires, counted from <paramref name="fromUtc"/>; null when a clock does not end it.</summary>
    public static DateTimeOffset? ExpiresUtc(RaidMarkLifetime lifetime, DateTimeOffset fromUtc) => lifetime switch
    {
        RaidMarkLifetime.Ping => fromUtc + MapMarkPolicy.PingLifetime,
        RaidMarkLifetime.FiveMinutes => fromUtc + TimeSpan.FromMinutes(5),
        RaidMarkLifetime.FifteenMinutes => fromUtc + TimeSpan.FromMinutes(15),
        _ => null,
    };

    public static string Name(RaidMarkLifetime lifetime) => lifetime switch
    {
        RaidMarkLifetime.Ping => "Ping 45 s",
        RaidMarkLifetime.UntilRemoved => "Until removed",
        RaidMarkLifetime.FiveMinutes => "5 min",
        RaidMarkLifetime.FifteenMinutes => "15 min",
        RaidMarkLifetime.ThisRaid => "This raid",
        _ => lifetime.ToString(),
    };

    public static string ScopeName(RaidMarkScope scope) => scope == RaidMarkScope.Private ? "Just me" : "Squad";

    /// <summary>"4m 12s left", "until removed" or "this raid": what a row and a hover say about time.</summary>
    public static string TimeLeft(RaidMark mark, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (mark.State.ExpiresUtc is { } expires)
        {
            var left = expires - nowUtc;
            if (left <= TimeSpan.Zero)
            {
                return "expiring";
            }

            return left >= TimeSpan.FromMinutes(1)
                ? $"{(int)left.TotalMinutes}m {left.Seconds:00}s left"
                : $"{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))}s left";
        }

        return mark.Lifetime == RaidMarkLifetime.ThisRaid ? "this raid" : "until removed";
    }
}

/// <summary>Local pings and waypoints, added and moved from the raid map and kept between runs.</summary>
public interface IRaidMarkStore
{
    IReadOnlyList<RaidMark> Marks { get; }

    /// <summary>Raised after a load, add, move, or removal changes <see cref="Marks"/>.</summary>
    event Action? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task<RaidMark> AddAsync(
        RaidMarkKind kind,
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        CancellationToken cancellationToken = default);

    Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default);

    /// <summary>Sets a mark's custom name, or clears it back to numbered/"Ping" when null or blank.</summary>
    Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a mark with its scope and lifetime already set, in one change. Placing a private mark
    /// as a squad one and narrowing it afterwards would let the group forwarder send it first.
    /// </summary>
    /// <remarks>The lifetime decides the kind (<see cref="RaidMarkLifetimes.KindFor"/>).</remarks>
    Task<RaidMark> PlaceAsync(
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        RaidMarkScope scope,
        RaidMarkLifetime lifetime,
        CancellationToken cancellationToken = default,
        RaidMarkRoute? route = null);

    /// <summary>Changes who sees a mark and how long it lasts; a new lifetime counts from now.</summary>
    Task SetOptionsAsync(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default);

    /// <summary>Removes every "this raid" mark: the raid they belonged to is over.</summary>
    Task EndRaidAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// [#799] The PC's clock was set by <paramref name="jump"/>: moves every held creation and
    /// expiry time by it, so each mark keeps the time it had left. A store with no clock of its
    /// own has nothing to move.
    /// </summary>
    Task RebaseClockAsync(TimeSpan jump, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
