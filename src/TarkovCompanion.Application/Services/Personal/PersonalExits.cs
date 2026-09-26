namespace TarkovCompanion.Application.Services.Personal;

/// <summary>One of the player's own raids, as the exit and pattern counts read it.</summary>
/// <param name="UsedExtract">The exit the player recorded leaving by, or null.</param>
public sealed record PersonalRaid(
    Guid RaidId,
    string? MapId,
    string? Side,
    DateTimeOffset? StartedUtc,
    string? Outcome,
    string? UsedExtract);

/// <summary>An exit as the suggestion weighs it: where it is, and whether the raid offered it.</summary>
public sealed record ExitChoice(string Name, double Metres, bool IsOffered);

/// <summary>The exit suggested, and how many times the player has used it on this map and side.</summary>
public sealed record SuggestedExit(ExitChoice Exit, int Uses, bool UsesDecided);

/// <summary>
/// The exits the player actually used, per map and side, as a weight on "nearest exit" (#712 T8).
/// </summary>
/// <remarks>
/// A weight and never a filter: every exit offered stays offered, and an offered exit always wins
/// over one not seen offered, exactly as before. Among the candidates, one the player has used is
/// treated as up to <see cref="MaximumDiscount"/> nearer (<see cref="DiscountPerUse"/> a use), so a
/// habitual exit a little further off is named over a closer one they never take, but never one
/// more than a third further than the nearest. Only the local player's own recorded raids count.
/// </remarks>
public static class PersonalExits
{
    public const double DiscountPerUse = 0.05;

    public const double MaximumDiscount = 0.25;

    /// <summary>Times each exit was used on this map (and side, when both are known), by exit name.</summary>
    public static IReadOnlyDictionary<string, int> Uses(IEnumerable<PersonalRaid> raids, string mapId, string? side)
    {
        ArgumentNullException.ThrowIfNull(raids);
        return raids
            .Where(raid => string.Equals(raid.MapId, mapId, StringComparison.OrdinalIgnoreCase)
                && raid.UsedExtract is { Length: > 0 }
                && (side is null || raid.Side is null || string.Equals(raid.Side, side, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(raid => raid.UsedExtract!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The exit to name: offered first, then the nearest after the player's own use is weighed in.
    /// </summary>
    /// <returns>Null when no exit has a finite distance.</returns>
    public static SuggestedExit? Choose(IReadOnlyList<ExitChoice> exits, IReadOnlyDictionary<string, int> uses)
    {
        ArgumentNullException.ThrowIfNull(exits);
        ArgumentNullException.ThrowIfNull(uses);
        var placed = exits.Where(exit => double.IsFinite(exit.Metres) && exit.Metres >= 0).ToArray();
        var pool = placed.Any(exit => exit.IsOffered) ? placed.Where(exit => exit.IsOffered).ToArray() : placed;
        if (pool.Length == 0)
        {
            return null;
        }

        var nearest = pool.MinBy(exit => exit.Metres)!;
        var chosen = pool.MinBy(exit => Weighted(exit, uses))!;
        var count = uses.TryGetValue(chosen.Name, out var n) ? n : 0;
        return new SuggestedExit(chosen, count, !ReferenceEquals(chosen, nearest));
    }

    /// <summary>The exit used most on this map, with its count; null when none is recorded.</summary>
    public static (string Name, int Uses)? Favourite(IReadOnlyDictionary<string, int> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        return uses.Count == 0
            ? null
            : uses.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => ((string, int)?)(pair.Key, pair.Value)).First();
    }

    private static double Weighted(ExitChoice exit, IReadOnlyDictionary<string, int> uses)
    {
        var count = uses.TryGetValue(exit.Name, out var n) ? n : 0;
        return exit.Metres * (1 - Math.Min(MaximumDiscount, count * DiscountPerUse));
    }
}
