using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Application.Services.Events;

namespace TarkovCompanion.Application.Services;

public sealed class RecommendationEngine : IRecommendationEngine
{
    public RecommendationResult Recommend(
        ItemDefinition item,
        ItemPriceSnapshot price,
        RecommendationContext context,
        ValueTierThresholds thresholds,
        ActiveEventRules? eventRules = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thresholds);

        var activeRules = eventRules ?? ActiveEventRules.Empty;
        var eventRuleExplanation = EconomicRuleExplanation(price, activeRules);
        price = EventRulePriceAdjustment.Apply(price, activeRules);

        var economicValue = price.BestEconomicValue;
        var valuePerSlot = economicValue / item.Dimensions.Slots;
        var tier = thresholds.GetTier(valuePerSlot);
        var reasons = new List<RecommendationReason>();
        var action = ResolvePriority(item, price, context, valuePerSlot, thresholds, reasons);
        if (eventRuleExplanation is not null)
        {
            reasons.Add(new(RecommendationReasonCode.ActiveEventRule, eventRuleExplanation, 8));
        }
        var confidence = economicValue == 0
            ? new Confidence(Math.Min(context.ContextConfidence.Value, 0.60))
            : context.ContextConfidence;

        var explanation = string.Join(' ', reasons
            .OrderBy(x => x.Priority)
            .Select(x => x.Explanation));

        return new(
            action,
            tier,
            confidence,
            reasons.OrderBy(x => x.Priority).ToArray(),
            explanation,
            economicValue,
            valuePerSlot,
            price.Provenance.SourceUpdatedUtc ?? price.Provenance.ObservedUtc,
            price.BestSaleChannel);
    }

    private static string? EconomicRuleExplanation(ItemPriceSnapshot price, ActiveEventRules rules)
    {
        var effects = new List<string>();
        if (!rules.FleaEnabled && price.FleaPriceRoubles is not null)
        {
            effects.Add("the flea is closed");
        }

        foreach (var offer in price.TraderOffers)
        {
            var multiplier = rules.TraderMultiplier(offer.TraderId);
            if (multiplier != 1m)
            {
                effects.Add($"{offer.TraderName} prices are x{multiplier:0.##}");
            }
        }

        return effects.Count == 0
            ? null
            : $"Active event rules apply: {string.Join("; ", effects.Distinct(StringComparer.Ordinal))}.";
    }

    private static RecommendationAction ResolvePriority(
        ItemDefinition item,
        ItemPriceSnapshot price,
        RecommendationContext context,
        long valuePerSlot,
        ValueTierThresholds thresholds,
        List<RecommendationReason> reasons)
    {
        if (!string.IsNullOrWhiteSpace(context.UserOverride) &&
            Enum.TryParse<RecommendationAction>(context.UserOverride, true, out var overrideAction))
        {
            reasons.Add(new(RecommendationReasonCode.UserOverride, $"Your item override selects {overrideAction}.", 0));
            return overrideAction;
        }

        if (context.EventState == EventItemState.Allergic)
        {
            reasons.Add(new(RecommendationReasonCode.KnownAllergy, "You marked this item as allergic; do not consume it.", 1));
            return RecommendationAction.AvoidConsume;
        }

        if (context.OutstandingFoundInRaidQuestCount > 0 && context.IsFoundInRaid)
        {
            reasons.Add(new(
                RecommendationReasonCode.OutstandingQuestFoundInRaid,
                $"An outstanding quest still needs {context.OutstandingFoundInRaidQuestCount} found-in-raid.",
                2));
            return RecommendationAction.EssentialKeep;
        }

        if (context.OutstandingQuestCount > 0)
        {
            reasons.Add(new(RecommendationReasonCode.OutstandingQuest, $"Outstanding quests still need {context.OutstandingQuestCount}.", 3));
            return RecommendationAction.EssentialKeep;
        }

        if (context.OutstandingHideoutCount > 0)
        {
            reasons.Add(new(RecommendationReasonCode.HideoutRequirement, $"A planned hideout upgrade still needs {context.OutstandingHideoutCount}.", 4));
            return RecommendationAction.Keep;
        }

        if (context.IsWishlisted)
        {
            reasons.Add(new(RecommendationReasonCode.Wishlist, "This item is on your wishlist.", 5));
            return RecommendationAction.Keep;
        }

        if (context.EventState == EventItemState.Untested && item.Category is ItemCategory.Provision or ItemCategory.Medicine)
        {
            reasons.Add(new(RecommendationReasonCode.ActiveEventRule, "This event item has not been tested yet.", 6));
            return RecommendationAction.EventTestCandidate;
        }

        if (!string.IsNullOrWhiteSpace(context.SpecializedAdvice))
        {
            var code = item.Category is ItemCategory.Ammunition or ItemCategory.AmmunitionPack
                ? RecommendationReasonCode.AmmoQuality
                : RecommendationReasonCode.KeyUtility;
            reasons.Add(new(code, context.SpecializedAdvice, 7));
        }

        if (price.BestEconomicValue == 0)
        {
            reasons.Add(new(RecommendationReasonCode.MissingPriceData, "No current flea or trader price is cached.", 8));
            return RecommendationAction.Unknown;
        }

        if (valuePerSlot < thresholds.C)
        {
            reasons.Add(new(RecommendationReasonCode.LowValuePerSlot, $"Value per slot is {valuePerSlot:N0} roubles, below your C-tier threshold.", 9));
            return RecommendationAction.DropFirst;
        }

        if (price.BestSaleChannel == SaleChannel.Flea && item.FleaEligible)
        {
            reasons.Add(new(RecommendationReasonCode.BestFleaValue, "The flea price is the strongest currently known sale path.", 9));
            return RecommendationAction.SellFlea;
        }

        reasons.Add(new(RecommendationReasonCode.BestTraderValue, "The best trader offer is the strongest currently known sale path.", 9));
        return RecommendationAction.SellTrader;
    }
}
