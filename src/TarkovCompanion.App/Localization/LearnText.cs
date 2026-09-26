namespace TarkovCompanion.App.Localization;

// #712 1-12: naming a refused cell or a misread flea item, and Setup's row for what was learned.
public static class LearnText
{
    public static string ItIs => UiText.Get("Learn.ItIs");
    public static string ItIsTip => UiText.Get("Learn.ItIsTip");
    public static string FleaItIs => UiText.Get("Learn.FleaItIs");
    public static string FleaItIsTip => UiText.Get("Learn.FleaItIsTip");
    public static string Heading => UiText.Get("Learn.Heading");
    public static string Counts(int icons, int names) => UiText.Format("Learn.Counts", icons, names);
    public static string KeepCrops => UiText.Get("Learn.KeepCrops");
    public static string KeepCropsNote => UiText.Get("Learn.KeepCropsNote");
    public static string DeleteAll => UiText.Get("Learn.DeleteAll");
    public static string ClearIcons => UiText.Get("Learn.ClearIcons");
    public static string ClearNames => UiText.Get("Learn.ClearNames");
    public static string Deleted => UiText.Get("Learn.Deleted");
    public static string DeleteFailed(string reason) => UiText.Format("Learn.DeleteFailed", reason);
}
