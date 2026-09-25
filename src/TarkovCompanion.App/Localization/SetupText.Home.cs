namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string HomeHeroTitle => UiText.Get("Setup.Home.Hero.Title");
    public static string HomeMapEyebrow => UiText.Get("Setup.Home.Map.Eyebrow");
    public static string HomeMapNone => UiText.Get("Setup.Home.Map.None");
    public static string HomeMapDetail => UiText.Get("Setup.Home.Map.Detail");
    public static string HomeMapExplore(object? arg0) => UiText.Format("Setup.Home.Map.Explore", arg0);
    public static string HomeMapOpenRaid => UiText.Get("Setup.Home.Map.OpenRaid");
    public static string HomeStepsHeading => UiText.Get("Setup.Home.Steps.Heading");
    public static string HomeAllSettings => UiText.Get("Setup.Home.AllSettings");
    public static string HomePlanHeading => UiText.Get("Setup.Home.Plan.Heading");
    public static string HomePlanTitle(object? arg0, object? arg1) => UiText.Format("Setup.Home.Plan.Title", arg0, arg1);
    public static string HomePlanEmpty => UiText.Get("Setup.Home.Plan.Empty");
    public static string HomePlanUnavailable => UiText.Get("Setup.Home.Plan.Unavailable");
    public static string HomePlanOpen => UiText.Get("Setup.Home.Plan.Open");
    public static string HomeHealthHeading => UiText.Get("Setup.Home.Health.Heading");
    public static string HomeHealthReady(object? arg0) => UiText.Format("Setup.Home.Health.Ready", arg0);
    public static string HomeHealthNeedsAction(object? arg0) => UiText.Format("Setup.Home.Health.NeedsAction", arg0);
    public static string HomeHealthUnconfirmed(object? arg0) => UiText.Format("Setup.Home.Health.Unconfirmed", arg0);
    public static string HomeHealthClear => UiText.Get("Setup.Home.Health.Clear");
    public static string HomeRecentHeading => UiText.Get("Setup.Home.Recent.Heading");
    public static string HomeRecentRaids => UiText.Get("Setup.Home.Recent.Raids");
    public static string HomeRecentRaid(object? arg0, object? arg1) => UiText.Format("Setup.Home.Recent.Raid", arg0, arg1);
    public static string HomeRecentEmpty => UiText.Get("Setup.Home.Recent.Empty");
    public static string HomeRecentOpen => UiText.Get("Setup.Home.Recent.Open");
    public static string HomePrivacyHeading => UiText.Get("Setup.Home.Privacy.Heading");
    public static string HomePrivacyCleanupOnDetail(object? arg0) => UiText.Format("Setup.Home.Privacy.CleanupOnDetail", arg0);
    public static string HomePrivacyCleanupOffDetail => UiText.Get("Setup.Home.Privacy.CleanupOffDetail");
    public static string HomePrivacyReview => UiText.Get("Setup.Home.Privacy.Review");
}
