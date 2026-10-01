using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public enum EarlyRaidSpawnPhase
{
    Unaffected,
    Active,
    Expired,
}

/// <summary>What the opening window shows, and why.</summary>
/// <param name="Phase">Whether the window is open.</param>
/// <param name="Areas">The PMC spawn areas to draw; empty unless the window is open.</param>
public sealed record EarlyRaidSpawnSelection(
    EarlyRaidSpawnPhase Phase,
    IReadOnlyList<NearbySpawn> Areas)
{
    /// <summary>
    /// Measured from the raid's first screenshot. False before one: every PMC spawn area on the
    /// map, none of them nearer than another.
    /// </summary>
    public bool IsNearby { get; init; }

    /// <summary>Nothing has said which side this raid is, and it is shown as a PMC raid.</summary>
    public bool SideAssumed { get; init; }

    /// <summary>Where the five minutes are counted from: the game starting, or the raid's start.</summary>
    public DateTimeOffset? OpenedUtc { get; init; }

    /// <summary>When the window closes.</summary>
    public DateTimeOffset? ClosesUtc { get; init; }
}

/// <summary>Possible PMC spawn areas during the raid's opening five minutes.</summary>
/// <remarks>
/// <para>
/// The time comes from the same injected clock as the rest of the raid workspace. A scav run is
/// deliberately unaffected: it joins after the opening and the map already suppresses its spawn
/// layer.
/// </para>
/// <para>
/// [#985] "i also still dont see the nearby pmc spawns when raid starts", reported three times
/// (#664, #914, #985). Each gate below hid them on the owner's real raids; what is left is the
/// least a player has to do to see them, which is nothing:
/// </para>
/// <list type="bullet">
/// <item>An unknown side counts as PMC here. A transit or offline raid has no notification to name
/// its side, and two of his Labs raids on 2026-09-23 were exactly that; drawing nothing reads as a
/// broken feature, so it is drawn, and the caller says the side was assumed. Only a raid known to
/// be a scav run is kept clean.</item>
/// <item>No screenshot is needed. Before the first one every PMC spawn area of the map is shown;
/// the first one narrows them to the nearby ones (<see cref="IsNearby"/>).</item>
/// <item>The five minutes run from <c>GameStarted</c>, the moment the player can move, when the
/// log has said so. The raid "starts" on the confirmation, before that (58 s to 2 min 57 s
/// earlier on his logs of 2026-09-23 to 25), and a window counted from there spent up to half
/// of itself on the loading screen.</item>
/// </list>
/// </remarks>
public sealed class EarlyRaidSpawnPolicy(TimeProvider timeProvider)
{
    public static readonly TimeSpan VisibleFor = TimeSpan.FromMinutes(5);

    /// <summary>A <c>GameStarted</c> further than this after the raid's start is not this raid's.</summary>
    private static readonly TimeSpan LongestLoad = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Groups and measures from the map's own features.</summary>
    /// <param name="raidStart">The first screenshot's position; null before there is one.</param>
    public EarlyRaidSpawnSelection Select(
        IReadOnlyList<MapFeature> features,
        WorldPosition? raidStart,
        WorldPosition? player,
        MapFeatureFaction side,
        DateTimeOffset? startedUtc,
        DateTimeOffset? gameStartedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        var window = Window(side, startedUtc, gameStartedUtc);
        if (window.Phase != EarlyRaidSpawnPhase.Active)
        {
            return window;
        }

        return raidStart is { } start
            ? window with { Areas = SpawnProximity.Near(features, start, player, MapFeatureFaction.Pmc), IsNearby = true }
            : window with { Areas = SpawnProximity.PmcAreas(features) };
    }

    /// <summary>Applies the time/side rule to areas the map has already grouped.</summary>
    /// <param name="areas">The areas near the first screenshot, or every PMC area before one.</param>
    /// <param name="isNearby">Whether <paramref name="areas"/> were measured from a first screenshot.</param>
    public EarlyRaidSpawnSelection Select(
        IReadOnlyList<NearbySpawn> areas,
        MapFeatureFaction side,
        DateTimeOffset? startedUtc,
        DateTimeOffset? gameStartedUtc = null,
        bool isNearby = true)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var window = Window(side, startedUtc, gameStartedUtc);
        return window.Phase == EarlyRaidSpawnPhase.Active
            // A list built for an unknown side also holds the scav-only areas; these are PMC spawns.
            ? window with { Areas = [.. areas.Where(area => area.Side != MapFeatureFaction.Scav)], IsNearby = isNearby }
            : window;
    }

    /// <summary>Where the five minutes are counted from.</summary>
    public static DateTimeOffset OpensAt(DateTimeOffset startedUtc, DateTimeOffset? gameStartedUtc) =>
        gameStartedUtc is { } moving && moving > startedUtc && moving - startedUtc <= LongestLoad
            ? moving
            : startedUtc;

    private EarlyRaidSpawnSelection Window(MapFeatureFaction side, DateTimeOffset? startedUtc, DateTimeOffset? gameStartedUtc)
    {
        if (side == MapFeatureFaction.Scav || startedUtc is not { } started)
        {
            return new(EarlyRaidSpawnPhase.Unaffected, []);
        }

        var opened = OpensAt(started, gameStartedUtc);
        var closes = opened + VisibleFor;
        return new(_timeProvider.GetUtcNow() < closes ? EarlyRaidSpawnPhase.Active : EarlyRaidSpawnPhase.Expired, [])
        {
            SideAssumed = side != MapFeatureFaction.Pmc,
            OpenedUtc = opened,
            ClosesUtc = closes,
        };
    }
}
