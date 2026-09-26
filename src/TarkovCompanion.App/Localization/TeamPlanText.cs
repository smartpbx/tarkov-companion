namespace TarkovCompanion.App.Localization;

/// <summary>[#712 T7] The words of Team's shared-task planner and loadout ready check.</summary>
public static class TeamPlanText
{
    public static string Heading => UiText.Get("Team.Plan.Heading");
    public static string Source => UiText.Get("Team.Plan.Source");
    public static string CountedPerQuest => UiText.Get("Team.Plan.CountedPerQuest");
    public static string PutOnRaid => UiText.Get("Team.Plan.PutOnRaid");
    public static string Together => UiText.Get("Team.Plan.Together");
    public static string None => UiText.Get("Team.Plan.None");
    public static string NoneHint => UiText.Get("Team.Plan.NoneHint");
    public static string OtherMaps => UiText.Get("Team.Plan.OtherMaps");
    public static string ReadySource => UiText.Get("Team.Ready.Source");
    public static string NothingShared => UiText.Get("Team.Ready.NothingShared");
    public static string NothingToCheck => UiText.Get("Team.Ready.NothingToCheck");
    public static string AllThere => UiText.Get("Team.Ready.AllThere");
    public static string ShareReadyCheckToggle => UiText.Get("Team.ShareReadyCheckToggle");
    public static string ShareMyReadyCheck => UiText.Get("Team.ShareMyReadyCheck");
    public static string ShareReadyCheckHint => UiText.Get("Team.ShareReadyCheckHint");

    /// <summary>"Customs · 5 shared quests".</summary>
    public static string Title(string map, int count) =>
        UiText.Format("Team.Plan.Title", map, UiText.Plural("Team.Plan.SharedQuests", count));

    /// <summary>"Woods · 2", a chip for another map with shared quests.</summary>
    public static string MapCount(string map, int count) => UiText.Format("Team.Plan.MapCount", map, count);

    /// <summary>"keys", "items", "weapon", "gear"; an unknown kind from a newer companion as it came.</summary>
    public static string Kind(string kind) => kind switch
    {
        "keys" => UiText.Get("Team.Ready.Kind.keys"),
        "items" => UiText.Get("Team.Ready.Kind.items"),
        "weapon" => UiText.Get("Team.Ready.Kind.weapon"),
        "gear" => UiText.Get("Team.Ready.Kind.gear"),
        _ => kind,
    };

    public static string Missing(string item) => UiText.Format("Team.Ready.Missing", item);
    public static string NotScanned(string kind) => UiText.Format("Team.Ready.NotScanned", kind);
    public static string Level(int level) => UiText.Format("Team.Ready.Level", level);
    public static string SharedBy(string name, string ago) => UiText.Format("Team.Ready.SharedBy", name, ago);
    public static string CheckedHere(string ago) => UiText.Format("Team.Ready.CheckedHere", ago);
    public static string Summary(int ready, int total) => UiText.Format("Team.Ready.Summary", ready, total);
    public static string MemberMissing(string name, int count) => UiText.Plural("Team.Ready.MemberMissing", count, name);
}
