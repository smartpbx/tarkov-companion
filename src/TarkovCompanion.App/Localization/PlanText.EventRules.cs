using System.Globalization;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// An event's rules in a line (#314): "Prapor prices x1.2; Flea closed; Customs open". Moved from
/// the Application layer, which only ever built words here.
/// </summary>
public static partial class PlanText
{
    private const int VisibleEventEffectLimit = 4;

    /// <summary>The rules being edited, separated by "; ".</summary>
    public static string EventRulePreview(EventRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return JoinBounded(rules.Effects.Select(DescribeEventEffect), "; ");
    }

    /// <summary>The rules in force now, separated by " · ".</summary>
    public static string EventRuleActiveSummary(ActiveEventRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return JoinBounded(rules.Rules.Select(rule => DescribeEventEffect(rule.Effect)), " · ");
    }

    private static string JoinBounded(IEnumerable<string> values, string separator)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        var shown = string.Join(separator, distinct.Take(VisibleEventEffectLimit));
        return distinct.Length > VisibleEventEffectLimit
            ? shown + separator + UiText.Format("Plan.EventRule.More", distinct.Length - VisibleEventEffectLimit)
            : shown;
    }

    private static string DescribeEventEffect(EventRuleEffect effect) => effect switch
    {
        TraderPriceMultiplierRule rule => UiText.Format("Plan.EventRule.TraderPrices", rule.TraderName, Multiplier(rule.Multiplier)),
        FleaAvailabilityRule { Enabled: true } => UiText.Get("Plan.EventRule.FleaOpen"),
        FleaAvailabilityRule { Enabled: false } => UiText.Get("Plan.EventRule.FleaClosed"),
        MapAvailabilityRule rule => UiText.Format(rule.Available ? "Plan.EventRule.MapOpen" : "Plan.EventRule.MapClosed", rule.MapName),
        BossSpawnMultiplierRule rule => UiText.Format("Plan.EventRule.BossSpawns", rule.BossName, Multiplier(rule.Multiplier)),
        QuestAvailabilityWindowRule rule => QuestWindow(rule),
        _ => UiText.Get("Plan.EventRule.Unknown"),
    };

    private static string Multiplier(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static string QuestWindow(QuestAvailabilityWindowRule rule) => (rule.StartUtc, rule.EndUtc) switch
    {
        ({ } start, { } end) => UiText.Format("Plan.EventRule.QuestBetween", rule.QuestName, LocalTime.Date(start), LocalTime.Date(end)),
        ({ } start, null) => UiText.Format("Plan.EventRule.QuestFrom", rule.QuestName, LocalTime.Date(start)),
        (null, { } end) => UiText.Format("Plan.EventRule.QuestUntil", rule.QuestName, LocalTime.Date(end)),
        _ => UiText.Format("Plan.EventRule.QuestUnchanged", rule.QuestName),
    };
}
