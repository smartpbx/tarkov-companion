using TarkovCompanion.Core.Domain.Profiles;
using V2RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>What an item rule in the profile says about picking the item up.</summary>
public enum LootScanItemRule
{
    /// <summary>No rule for this item.</summary>
    None,

    AlwaysTake,

    AlwaysLeave,

    /// <summary>A rule is recorded and this build cannot read it, which is not the same as none.</summary>
    Unrecognised,
}

/// <summary>
/// How the profile's pins, wishlist and item rules read for one item, and how to change them.
/// </summary>
/// <remarks>
/// <para>
/// The Loot Scan told the engine "not pinned, not protected, no rule" for every item at full
/// confidence without opening the profile, so an item the player had pinned was shown as
/// unpinned and weighed on price. The profile has held all three since the V2 data platform:
/// <see cref="ProfileProgress.Pins"/> with a free-text kind, the wishlist, and one text rule
/// per item. This is the one place that says what those texts mean.
/// </para>
/// <para>
/// A pin is a <see cref="ProfilePin"/> of kind <c>item</c>. A rule is the text under the item's
/// id: <c>Protected</c>, a take rule, or a leave rule. The V1 names are read too, because V2
/// profiles are migrated from V1 ones: keeping something means taking it when it is found. A
/// sell rule is about the stash and says nothing about picking the item up, so it reads as no
/// rule here. Any other text is reported as unrecognised rather than dropped.
/// </para>
/// </remarks>
public static class LootScanProfileRules
{
    public const string ItemPinKind = "item";
    public const string ProtectedRule = "Protected";
    public const string TakeRule = "Take";
    public const string LeaveRule = "Leave";

    public static bool IsPinned(ProfileProgress progress, string itemId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.Pins.Any(pin =>
            string.Equals(pin.TargetKind, ItemPinKind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pin.TargetId, itemId, StringComparison.Ordinal));
    }

    public static bool IsWishlisted(ProfileProgress progress, string itemId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.WishlistItemIds.Contains(itemId, StringComparer.Ordinal);
    }

    public static bool IsProtected(ProfileProgress progress, string itemId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.ItemOverrides.TryGetValue(itemId, out var rule) &&
               string.Equals(rule.Trim(), ProtectedRule, StringComparison.OrdinalIgnoreCase);
    }

    public static LootScanItemRule RuleFor(ProfileProgress progress, string itemId)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!progress.ItemOverrides.TryGetValue(itemId, out var text))
        {
            return LootScanItemRule.None;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "protected" => LootScanItemRule.None,
            "take" or "keep" or "essentialkeep" or "use" => LootScanItemRule.AlwaysTake,
            "leave" or "dropfirst" => LootScanItemRule.AlwaysLeave,
            "sellflea" or "selltrader" or "sellonflea" or "selltotrader" => LootScanItemRule.None,
            _ => LootScanItemRule.Unrecognised,
        };
    }

    public static V2RecommendationAction? ActionFor(LootScanItemRule rule) => rule switch
    {
        LootScanItemRule.AlwaysTake => V2RecommendationAction.Take,
        LootScanItemRule.AlwaysLeave => V2RecommendationAction.Leave,
        _ => null,
    };

    public static ProfileProgress WithPin(ProfileProgress progress, string itemId, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var others = progress.Pins
            .Where(pin => !(string.Equals(pin.TargetKind, ItemPinKind, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(pin.TargetId, itemId, StringComparison.Ordinal)))
            .ToList();
        if (pinned)
        {
            others.Add(new(ItemPinKind, itemId, others.Count == 0 ? 0 : others.Max(pin => pin.SortOrder) + 1, null));
        }

        return Copy(progress, pins: others);
    }

    public static ProfileProgress WithWishlist(ProfileProgress progress, string itemId, bool wishlisted)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var items = progress.WishlistItemIds.Where(id => !string.Equals(id, itemId, StringComparison.Ordinal)).ToList();
        if (wishlisted)
        {
            items.Add(itemId);
        }

        return Copy(progress, wishlist: items);
    }

    /// <param name="rule">The rule text to record, or null to remove whatever rule the item has.</param>
    public static ProfileProgress WithRule(ProfileProgress progress, string itemId, string? rule)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var rules = progress.ItemOverrides
            .Where(pair => !string.Equals(pair.Key, itemId, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(rule))
        {
            rules[itemId] = rule.Trim();
        }

        return Copy(progress, rules: rules);
    }

    private static ProfileProgress Copy(
        ProfileProgress progress,
        IReadOnlyList<ProfilePin>? pins = null,
        IReadOnlyCollection<string>? wishlist = null,
        IReadOnlyDictionary<string, string>? rules = null) =>
        new(
            progress.Level,
            progress.TraderLevels,
            progress.CompletedTaskIds,
            progress.ObjectiveProgress,
            progress.HideoutStationLevels,
            wishlist ?? progress.WishlistItemIds,
            progress.OwnedItemCounts,
            progress.EventItemStates,
            rules ?? progress.ItemOverrides,
            pins ?? progress.Pins);
}
