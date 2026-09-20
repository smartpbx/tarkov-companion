using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Hideout upgrades over more than one level (#307): the order a chosen station level has to be
/// built in, the upgrades that come next for a profile, and one shopping list across several of
/// them. <see cref="HideoutPlanner"/> still answers "what does the next level of each station ask
/// for"; this answers "and after that".
/// </summary>
public static class HideoutUpgradePlanner
{
    /// <summary>
    /// Every unbuilt level between the profile and <paramref name="targetLevel"/> of one station,
    /// prerequisites first. A station level comes after the level below it and after every
    /// station level it names; a level the catalog does not list is skipped rather than invented,
    /// and a prerequisite cycle (none exists upstream today) stops at the repeat instead of looping.
    /// </summary>
    public static IReadOnlyList<HideoutUpgradeStep> CriticalPath(
        IEnumerable<HideoutStationSummary> stations,
        IReadOnlyDictionary<string, int> builtLevels,
        IEnumerable<HideoutItemRequirement> requirements,
        HideoutPrerequisites prerequisites,
        IReadOnlyDictionary<string, int> owned,
        string targetStationId,
        int targetLevel)
    {
        var context = new Context(stations, builtLevels, requirements, prerequisites, owned);
        var steps = new List<HideoutUpgradeStep>();
        var seen = new HashSet<(string, int)>();
        Visit(context, targetStationId, targetLevel, seen, steps);
        return steps;
    }

    /// <summary>
    /// The next <paramref name="count"/> upgrades, in the order they become possible: first every
    /// level whose station prerequisites are already built, then what those unlock, and so on.
    /// Inside one round the upgrade the player is closest to comes first.
    /// </summary>
    public static IReadOnlyList<HideoutUpgradeStep> NextUpgrades(
        IEnumerable<HideoutStationSummary> stations,
        IReadOnlyDictionary<string, int> builtLevels,
        IEnumerable<HideoutItemRequirement> requirements,
        HideoutPrerequisites prerequisites,
        IReadOnlyDictionary<string, int> owned,
        int count)
    {
        var context = new Context(stations, builtLevels, requirements, prerequisites, owned);
        var simulated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in context.Stations.Values)
        {
            simulated[station.StationId] = builtLevels.GetValueOrDefault(station.StationId);
        }

        var steps = new List<HideoutUpgradeStep>();
        while (steps.Count < count)
        {
            var round = context.Stations.Values
                .Select(station => (station, next: NextLevel(station, simulated[station.StationId])))
                .Where(entry => entry.next > 0 && context
                    .StationPrerequisites(entry.station.StationId, entry.next)
                    .All(required => simulated.GetValueOrDefault(required.RequiredStationId) >= required.RequiredLevel))
                .Select(entry => context.Step(entry.station, entry.next))
                .OrderBy(step => step.MissingItemCount + step.UnknownItemCount)
                .ThenBy(step => step.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(step => step.StationId, StringComparer.Ordinal)
                .ToArray();
            if (round.Length == 0)
            {
                break;
            }

            foreach (var step in round)
            {
                simulated[step.StationId] = step.Level;
            }

            steps.AddRange(round.Take(count - steps.Count));
        }

        return steps;
    }

    /// <summary>
    /// The next level of every station that has one, whether or not it can be started yet: what
    /// the page's "still needed across stations" list has always covered.
    /// </summary>
    public static IReadOnlyList<HideoutUpgradeStep> EveryNextLevel(
        IEnumerable<HideoutStationSummary> stations,
        IReadOnlyDictionary<string, int> builtLevels,
        IEnumerable<HideoutItemRequirement> requirements,
        HideoutPrerequisites prerequisites,
        IReadOnlyDictionary<string, int> owned)
    {
        var context = new Context(stations, builtLevels, requirements, prerequisites, owned);
        return
        [
            .. context.Stations.Values
                .Select(station => (station, next: NextLevel(station, builtLevels.GetValueOrDefault(station.StationId))))
                .Where(entry => entry.next > 0)
                .Select(entry => context.Step(entry.station, entry.next))
                .OrderBy(step => step.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// One line per item across <paramref name="steps"/>. Amounts are summed before the holding is
    /// taken off, because one pile of bolts serves whichever step is built first.
    /// </summary>
    public static IReadOnlyList<HideoutShoppingLine> ShoppingList(
        IEnumerable<HideoutUpgradeStep> steps,
        IReadOnlyDictionary<string, int> owned)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(owned);
        return
        [
            .. steps
                .SelectMany(step => step.Needs)
                .GroupBy(need => need.ItemId, StringComparer.Ordinal)
                .Select(group => new HideoutShoppingLine(
                    group.Key,
                    group.Sum(need => need.Required),
                    HeldCount.Of(owned, group.Key),
                    group.Count()))
                .Where(line => line.Remaining > 0)
                .OrderBy(line => line.ItemId, StringComparer.Ordinal),
        ];
    }

    private static void Visit(
        Context context,
        string stationId,
        int level,
        HashSet<(string, int)> seen,
        List<HideoutUpgradeStep> steps)
    {
        if (!context.Stations.TryGetValue(stationId, out var station) ||
            level <= context.Built.GetValueOrDefault(station.StationId) ||
            !station.Levels.Contains(level) ||
            !seen.Add((station.StationId.ToUpperInvariant(), level)))
        {
            return;
        }

        var below = station.Levels.Where(candidate => candidate < level).DefaultIfEmpty(0).Max();
        if (below > 0)
        {
            Visit(context, station.StationId, below, seen, steps);
        }

        foreach (var required in context.StationPrerequisites(station.StationId, level))
        {
            Visit(context, required.RequiredStationId, required.RequiredLevel, seen, steps);
        }

        steps.Add(context.Step(station, level));
    }

    private static int NextLevel(HideoutStationSummary station, int built) =>
        station.Levels.Where(level => level > built).DefaultIfEmpty(0).Min();

    private sealed class Context
    {
        private readonly ILookup<(string, int), HideoutItemRequirement> _items;
        private readonly ILookup<(string, int), HideoutStationPrerequisite> _stationPrerequisites;
        private readonly ILookup<(string, int), HideoutOtherPrerequisite> _others;
        private readonly IReadOnlyDictionary<string, int> _owned;

        public Context(
            IEnumerable<HideoutStationSummary> stations,
            IReadOnlyDictionary<string, int> builtLevels,
            IEnumerable<HideoutItemRequirement> requirements,
            HideoutPrerequisites prerequisites,
            IReadOnlyDictionary<string, int> owned)
        {
            ArgumentNullException.ThrowIfNull(stations);
            ArgumentNullException.ThrowIfNull(requirements);
            ArgumentNullException.ThrowIfNull(prerequisites);
            Built = builtLevels ?? throw new ArgumentNullException(nameof(builtLevels));
            _owned = owned ?? throw new ArgumentNullException(nameof(owned));
            Stations = stations
                .GroupBy(station => station.StationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _items = requirements.ToLookup(requirement => Key(requirement.StationId, requirement.TargetLevel));
            _stationPrerequisites = prerequisites.Stations.ToLookup(required => Key(required.StationId, required.TargetLevel));
            _others = prerequisites.Others.ToLookup(other => Key(other.StationId, other.TargetLevel));
        }

        public IReadOnlyDictionary<string, HideoutStationSummary> Stations { get; }

        public IReadOnlyDictionary<string, int> Built { get; }

        public IEnumerable<HideoutStationPrerequisite> StationPrerequisites(string stationId, int level) =>
            _stationPrerequisites[Key(stationId, level)];

        public HideoutUpgradeStep Step(HideoutStationSummary station, int level) => new(
            station.StationId,
            station.Name,
            level,
            [.. _items[Key(station.StationId, level)].Select(requirement => new HideoutLevelNeed(
                requirement.ItemId,
                requirement.Required,
                HeldCount.Of(_owned, requirement.ItemId)))],
            [.. _others[Key(station.StationId, level)].Select(other => other.Label)]);

        private static (string, int) Key(string stationId, int level) => (stationId.ToUpperInvariant(), level);
    }
}
