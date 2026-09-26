using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.Localization;

/// <summary>The pre-raid brief's words (#712 0-9).</summary>
public static partial class RaidText
{
    public static string BriefWhileMatching => UiText.Get("Raid.Brief.WhileMatching");
    public static string BriefWhileLoading => UiText.Get("Raid.Brief.WhileLoading");
    public static string BriefWhileSpawning => UiText.Get("Raid.Brief.WhileSpawning");
    public static string BriefName => UiText.Get("Raid.Brief.Name");
    public static string BriefNoBosses => UiText.Get("Raid.Brief.NoBosses");
    public static string BriefQuestsHere => UiText.Get("Raid.Brief.QuestsHere");
    public static string BriefNoQuests => UiText.Get("Raid.Brief.NoQuests");
    public static string BriefCanStillLeave => UiText.Get("Raid.Brief.CanStillLeave");
    public static string BriefExtractsBothSides => UiText.Get("Raid.Brief.ExtractsBothSides");
    public static string BriefNoExtracts => UiText.Get("Raid.Brief.NoExtracts");
    public static string BriefOfferedHint => UiText.Get("Raid.Brief.OfferedHint");
    public static string BriefSquad => UiText.Get("Raid.Brief.Squad");
    public static string BriefSquadFrom => UiText.Get("Raid.Brief.SquadFrom");
    public static string BriefSquadFromGame => UiText.Get("Raid.Brief.SquadFromGame");
    public static string BriefPartyReady => UiText.Get("Raid.Brief.PartyReady");
    public static string BriefPartyNotReady => UiText.Get("Raid.Brief.PartyNotReady");

    public static string BriefSide(SituationSide side) => side switch
    {
        SituationSide.Pmc => UiText.Get("Raid.Brief.SidePmc"),
        SituationSide.Scav => UiText.Get("Raid.Brief.SideScav"),
        _ => UiText.Get("Raid.Brief.SideUnknown"),
    };

    public static string BriefMinutes(double minutes) => UiText.Format("Raid.Brief.Minutes", Math.Round(minutes));

    /// <summary>"Reshala 60%": a catalog rate, with "on a trigger" where the likeliest spawn waits for one.</summary>
    public static string BriefBossChance(string name, double chance, bool triggered) =>
        UiText.Format(triggered ? "Raid.Brief.BossTriggered" : "Raid.Brief.BossChance", name, Math.Round(chance * 100));

    public static string BriefBossesFromCatalog(string bosses) => UiText.Format("Raid.Brief.BossesFromCatalog", bosses);
    public static string BriefNeeds(string items) => UiText.Format("Raid.Brief.Needs", items);
    public static string BriefMoreQuests(int count) => UiText.Plural("Raid.Brief.MoreQuests", count);
    public static string BriefRoute(string steps) => UiText.Format("Raid.Brief.Route", steps);
    public static string BriefRouteStep(string letter, string label) => UiText.Format("Raid.Brief.RouteStep", letter, label);
    public static string BriefExtractsFor(string side) => UiText.Format("Raid.Brief.ExtractsFor", side);
    public static string BriefMoreExtracts(int count) => UiText.Plural("Raid.Brief.MoreExtracts", count);
    public static string BriefSquadMember(string name, string state) => UiText.Format("Raid.Brief.SquadMember", name, state);

    public static string BriefSquadState(SquadMemberState state) => state switch
    {
        SquadMemberState.OutOfRaid => UiText.Get("Raid.Brief.SquadOutOfRaid"),
        SquadMemberState.Loading => UiText.Get("Raid.Brief.SquadLoading"),
        SquadMemberState.InRaid => UiText.Get("Raid.Brief.SquadInRaid"),
        SquadMemberState.OnAnotherMap => UiText.Get("Raid.Brief.SquadOnAnotherMap"),
        SquadMemberState.Quiet => UiText.Get("Raid.Brief.SquadQuiet"),
        _ => UiText.Get("Raid.Brief.SquadUnknown"),
    };

    public static string BriefLoot(int spots, bool more) => UiText.Format(more ? "Raid.Brief.LootMany" : "Raid.Brief.Loot", spots);
}
