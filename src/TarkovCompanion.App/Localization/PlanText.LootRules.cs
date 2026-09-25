namespace TarkovCompanion.App.Localization;

/// <summary>[#902 P9] Plan › Keep › Loot rules.</summary>
public static partial class PlanText
{
    public static string LootRules => UiText.Get("Plan.LootRules.Title");
    public static string LootRulesCount(int count) => UiText.Format("Plan.LootRules.TitleCount", count);
    public static string LootRulesHint => UiText.Get("Plan.LootRules.Hint");
    public static string LootRulesEmpty => UiText.Get("Plan.LootRules.Empty");
    public static string LootRulePinned => UiText.Get("Plan.LootRules.Pinned");
    public static string LootRuleWishlist => UiText.Get("Plan.LootRules.Wishlist");
    public static string LootRuleAlwaysTake => UiText.Get("Plan.LootRules.AlwaysTake");
    public static string LootRuleAlwaysLeave => UiText.Get("Plan.LootRules.AlwaysLeave");
    public static string LootRuleUnrecognised => UiText.Get("Plan.LootRules.Unrecognised");
    public static string LootRuleRemove => UiText.Get("Plan.LootRules.Remove");
    public static string LootRuleRemoveNamed(string rule, string item) => UiText.Format("Plan.LootRules.RemoveNamed", rule, item);
}
