using System.Globalization;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// [#914] The rules behind the Raid map's lines from nearby PMC spawn areas to the player: which
/// radius, which areas, and how strongly a line is drawn as the opening window closes.
/// </summary>
/// <remarks>
/// <para>
/// A line starts at a spawn AREA (<see cref="SpawnGrouping"/>), never at a single point: five
/// points in one yard are one place where a group arrived together, and five lines from one yard
/// would read as five squads.
/// </para>
/// <para>
/// Everything here is modelled from the static map catalog. A line says "a PMC may have started
/// there", never "somebody is there"; nothing about any other player is observed.
/// </para>
/// </remarks>
public static class SpawnLines
{
    /// <summary>The radii the picker offers, in metres.</summary>
    /// <remarks>
    /// 50, 100 and 150 are the ones asked for in #914; 300 is what the spawn panel has always
    /// listed (<see cref="SpawnProximity.DefaultRadiusMetres"/>), so the widest choice shows
    /// every area the panel does and nothing it cannot.
    /// </remarks>
    public static IReadOnlyList<int> RadiusChoices { get; } = [50, 100, 150, 300];

    /// <summary>The radius on a map nobody has chosen one for.</summary>
    /// <remarks>
    /// 150 on every map: the widest of the three #914 asks for. No per-map default was measured
    /// to be better, so none is guessed; a map where it is wrong is one press away and the choice
    /// is remembered for that map.
    /// </remarks>
    public const int DefaultRadiusMetres = 150;

    /// <summary>Full strength for this long after the raid starts; then the line fades.</summary>
    /// <remarks>
    /// V1 drew its threat lines at full strength for three minutes. The window here is
    /// <see cref="EarlyRaidSpawnPolicy.VisibleFor"/> (five), so the lines fade over its last two
    /// and are gone exactly when the window closes rather than lingering after the markers.
    /// </remarks>
    public static readonly TimeSpan FullFor = TimeSpan.FromMinutes(3);

    /// <summary>A stored radius, or the default when it is missing or not one of the choices.</summary>
    public static int ParseRadius(string? stored) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metres) &&
        RadiusChoices.Contains(metres)
            ? metres
            : DefaultRadiusMetres;

    /// <summary>The areas within the radius of where the raid began, nearest first as given.</summary>
    /// <remarks>
    /// Measured from the raid's start, the same anchor the spawn panel uses, so "within 100 m"
    /// means the same thing in both places and a marker does not blink out as the player walks.
    /// </remarks>
    public static IReadOnlyList<NearbySpawn> Within(IReadOnlyList<NearbySpawn> areas, double radiusMetres)
    {
        ArgumentNullException.ThrowIfNull(areas);
        return [.. areas.Where(area => area.MetresFromStart <= radiusMetres)];
    }

    /// <summary>How strongly the lines are drawn, from 1 (full) to 0 (not drawn).</summary>
    /// <param name="sinceStart">Time since the raid started, by the raid workspace's clock.</param>
    public static double Strength(TimeSpan sinceStart)
    {
        if (sinceStart <= FullFor)
        {
            return 1;
        }

        var visibleFor = EarlyRaidSpawnPolicy.VisibleFor;
        if (sinceStart >= visibleFor)
        {
            return 0;
        }

        return 1 - ((sinceStart - FullFor) / (visibleFor - FullFor));
    }
}
