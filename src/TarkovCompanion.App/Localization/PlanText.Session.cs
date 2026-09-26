namespace TarkovCompanion.App.Localization;

/// <summary>[#712 2-3] Plan's session strip and hand-in reminders.</summary>
public static partial class PlanText
{
    public static string SessionTitle => UiText.Get("Plan.Session.Title");
    public static string SessionYouSaid => UiText.Get("Plan.Session.YouSaid");
    public static string SessionLengthName => UiText.Get("Plan.Session.LengthName");
    public static string SessionHours(string hours) => UiText.Format("Plan.Session.Hours", hours);
    public static string SessionRaid(int number, string map) => UiText.Format("Plan.Session.Raid", number, map);
    public static string SessionLootRun => UiText.Get("Plan.Session.LootRun");
    public static string SessionReplans => UiText.Get("Plan.Session.Replans");
    public static string SessionPerRaid(string duration) => UiText.Format("Plan.Session.PerRaid", duration);
    public static string SessionPerRaidMeasured(string duration) => UiText.Format("Plan.Session.PerRaidMeasured", duration);
    public static string SessionPlayed(int played, string left) => UiText.Plural("Plan.Session.Played", played, left);
    public static string SessionEmpty => UiText.Get("Plan.Session.Empty");
    public static string SessionNoTime => UiText.Get("Plan.Session.NoTime");
    public static string HandInTitle => UiText.Get("Plan.HandIn.Title");
    public static string HandInReady(string quest, string trader) => UiText.Format("Plan.HandIn.Ready", quest, trader);
    public static string HandInItemsHeld(string quest, string trader) => UiText.Format("Plan.HandIn.ItemsHeld", quest, trader);
    public static string HandInShort(string quest, string trader) => UiText.Format("Plan.HandIn.Short", quest, trader);
}
