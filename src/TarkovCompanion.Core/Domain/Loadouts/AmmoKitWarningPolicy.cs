namespace TarkovCompanion.Core.Domain.Loadouts;

/// <summary>
/// When a kit is expensive enough that weak ammunition in it is worth a warning.
/// </summary>
/// <remarks>
/// A heuristic, and one a player may disagree with: it says nothing about ballistics, only that a
/// kit costing <see cref="KitCostThresholdRoubles"/> or more is a poor place for a round in one of
/// <see cref="WeakTiers"/>. Both numbers are configuration and not constants in the service, so a
/// caller that holds a different opinion supplies its own. The kit cost it is compared with is the
/// complete total only: with any price missing there is no total, so there is no warning rather than
/// one built on a partial sum.
/// </remarks>
public sealed record AmmoKitWarningPolicy(IReadOnlySet<string> WeakTiers, long KitCostThresholdRoubles)
{
    /// <summary>C and D tier ammunition in a kit of 150,000 roubles or more.</summary>
    public static AmmoKitWarningPolicy Default { get; } = new(
        new HashSet<string>(StringComparer.Ordinal) { "C", "D" },
        150_000);

    public bool Warns(string ammoTier, long? completeKitCostRoubles) =>
        completeKitCostRoubles is { } cost && cost >= KitCostThresholdRoubles && WeakTiers.Contains(ammoTier);
}
