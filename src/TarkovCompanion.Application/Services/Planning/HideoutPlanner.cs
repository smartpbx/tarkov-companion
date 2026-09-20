using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// What raising each hideout station one level asks for, what the player can start now, and what
/// is still short across all of them.
/// </summary>
/// <remarks>
/// This lived in the Hideout view model until #307 moved it here. Only the next level of each
/// station is planned: a critical path over several levels, the tools and skills a level asks for,
/// and build time are not modelled yet, and this planner does not pretend to. A level the catalog
/// does not list is not a level: the built level is clamped by the caller, and a station whose
/// levels are all built has no next level rather than a made-up one.
/// </remarks>
public static class HideoutPlanner
{
    public static IReadOnlyList<HideoutStationPlan> Plan(
        IEnumerable<HideoutStationSummary> stations,
        IReadOnlyDictionary<string, int> builtLevels,
        IEnumerable<HideoutItemRequirement> requirements,
        IReadOnlyDictionary<string, int> owned)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(builtLevels);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(owned);

        var byStation = requirements
            .GroupBy(requirement => requirement.StationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var plans = new List<HideoutStationPlan>();
        foreach (var station in stations)
        {
            var built = builtLevels.GetValueOrDefault(station.StationId);
            var next = station.Levels.Where(level => level > built).DefaultIfEmpty(0).Min();
            var needs = next > 0 && byStation.TryGetValue(station.StationId, out var all)
                ? all
                    .Where(requirement => requirement.TargetLevel == next)
                    .Select(requirement => new HideoutLevelNeed(
                        requirement.ItemId,
                        requirement.Required,
                        owned.GetValueOrDefault(requirement.ItemId)))
                    .ToArray()
                : [];
            plans.Add(new HideoutStationPlan(
                station.StationId,
                station.Name,
                built,
                station.Levels.Count == 0 ? 0 : station.Levels.Max(),
                next,
                needs));
        }

        return plans;
    }

    /// <summary>
    /// What is still short across every station's next level. Required amounts are summed per item
    /// before the player's holding is taken off, because one pile of bolts serves whichever station
    /// is built first, not each of them in turn.
    /// </summary>
    public static IReadOnlyList<HideoutShortfall> Shortfall(
        IEnumerable<HideoutStationPlan> plans,
        IReadOnlyDictionary<string, int> owned)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(owned);

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var need in plans.Where(plan => plan.HasNextLevel).SelectMany(plan => plan.NextLevelNeeds))
        {
            totals[need.ItemId] = totals.GetValueOrDefault(need.ItemId) + need.Required;
        }

        return
        [
            .. totals
                .Select(total => new HideoutShortfall(total.Key, total.Value, owned.GetValueOrDefault(total.Key)))
                .Where(shortfall => shortfall.Remaining > 0)
                .OrderByDescending(shortfall => shortfall.Remaining)
                .ThenBy(shortfall => shortfall.ItemId, StringComparer.Ordinal),
        ];
    }
}
