using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.ViewModels.V2.LootScan;

/// <summary>
/// What the player can change from the Loot Scan workspace, each of which re-decides the scan
/// on screen.
/// </summary>
/// <remarks>
/// The engine has always ranked a pin, the wishlist and an item rule above price, and nothing
/// in the app could set one: the profile carried the fields and no screen wrote them. The raid
/// phase and risk are the same kind of input, the player's own, with nowhere to say them.
/// </remarks>
public interface ILootScanWorkspaceControls
{
    RecommendationRaidRisk Risk { get; }

    /// <summary>Null means the phase is counted from the raid clock.</summary>
    RecommendationRaidPhase? Phase { get; }

    bool IsPinned(string itemId);

    bool IsWishlisted(string itemId);

    LootScanItemRule RuleFor(string itemId);

    Task SetRiskAsync(RecommendationRaidRisk risk);

    Task SetPhaseAsync(RecommendationRaidPhase? phase);

    Task SetPinnedAsync(string itemId, bool pinned);

    Task SetWishlistedAsync(string itemId, bool wishlisted);

    /// <param name="rule">Always take, always leave, or <see cref="LootScanItemRule.None"/> to clear the rule.</param>
    Task SetRuleAsync(string itemId, LootScanItemRule rule);
}

/// <summary>One choice in a small picker: what it sets and what it is called.</summary>
public sealed record LootScanChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
