using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Personal;

/// <summary>What the player's own last raids on one map came to.</summary>
/// <param name="Raids">Raids counted, newest first, at most the number asked for.</param>
/// <param name="Deaths">Of those, raids the player recorded as a death.</param>
/// <param name="DeathPlace">Where most of those deaths were, from each raid's last screenshot; null when unplaced.</param>
/// <param name="DeathsAtPlace">Deaths placed there.</param>
/// <param name="Extracted">Raids recorded as survived or run through.</param>
public sealed record PersonalPattern(
    string MapId,
    int Raids,
    int Deaths,
    string? DeathPlace,
    int DeathsAtPlace,
    int Extracted);

/// <summary>
/// "Your last 5 Customs raids: 3 deaths near Dorms" (#712 T8, personal patterns).
/// </summary>
/// <remarks>
/// History, never a forecast: it counts what the local player recorded about their own raids and
/// says nothing about any other player. The raids passed in are the local raid history and
/// nothing else; a squadmate's companion shares positions, never raids, so none can reach here.
/// A death is placed by the raid's last screenshot, which is where the player last photographed
/// themselves, not where they died; the caller's wording says "near" for that reason.
/// </remarks>
public static class PersonalPatterns
{
    public const int DefaultRaids = 5;

    /// <param name="lastPosition">The raid's last screenshot position, or null.</param>
    /// <param name="placeOf">A place name for a position, or null when it has none.</param>
    public static PersonalPattern? Describe(
        IEnumerable<PersonalRaid> raids,
        string mapId,
        Func<Guid, WorldPosition?> lastPosition,
        Func<WorldPosition, string?> placeOf,
        int take = DefaultRaids)
    {
        ArgumentNullException.ThrowIfNull(raids);
        ArgumentNullException.ThrowIfNull(lastPosition);
        ArgumentNullException.ThrowIfNull(placeOf);
        var recent = raids
            .Where(raid => string.Equals(raid.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(raid => raid.StartedUtc ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, take))
            .ToArray();
        if (recent.Length == 0)
        {
            return null;
        }

        var buckets = recent.Select(raid => (Raid: raid, Bucket: RaidCoverage.Classify(raid.Outcome))).ToArray();
        var deaths = buckets.Where(entry => entry.Bucket == RaidOutcomeBucket.Died).Select(entry => entry.Raid).ToArray();
        var places = deaths
            .Select(raid => lastPosition(raid.RaidId) is { } at ? placeOf(at) : null)
            .Where(place => place is { Length: > 0 })
            .GroupBy(place => place!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        // One death somewhere is not a pattern; a place is named only when two or more share it.
        var named = places is { } top && top.Count() >= 2 ? top : null;
        return new PersonalPattern(
            mapId,
            recent.Length,
            deaths.Length,
            named?.Key,
            named?.Count() ?? 0,
            buckets.Count(entry => RaidCoverage.IsExtracted(entry.Bucket)));
    }
}
