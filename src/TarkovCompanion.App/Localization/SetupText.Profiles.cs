namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string ProgressExchangeTitle => UiText.Get("Setup.Progress.ExchangeTitle");
    public static string ProgressExchangeNote => UiText.Get("Setup.Progress.ExchangeNote");
    public static string ProgressExchangePath => UiText.Get("Setup.Progress.ExchangePath");
    public static string ProgressExport => UiText.Get("Setup.Progress.Export");
    public static string ProgressPreview => UiText.Get("Setup.Progress.Preview");
    public static string ProgressKeepLocal => UiText.Get("Setup.Progress.KeepLocal");
    public static string ProgressUseIncoming => UiText.Get("Setup.Progress.UseIncoming");
    public static string ProgressApply => UiText.Get("Setup.Progress.Apply");
    public static string ProgressUndo => UiText.Get("Setup.Progress.Undo");
    public static string ProgressHistoryTitle => UiText.Get("Setup.Progress.HistoryTitle");
    public static string ProgressTrackerTitle => UiText.Get("Setup.Progress.TrackerTitle");
    public static string ProgressTrackerNote => UiText.Get("Setup.Progress.TrackerNote");
    public static string ProgressTrackerToken => UiText.Get("Setup.Progress.TrackerToken");
    public static string ProgressTrackerConnect => UiText.Get("Setup.Progress.TrackerConnect");
    public static string ProgressTrackerRefresh => UiText.Get("Setup.Progress.TrackerRefresh");
    public static string ProgressTrackerDisconnect => UiText.Get("Setup.Progress.TrackerDisconnect");
    public static string ProfilesHeading => UiText.Get("Setup.Profiles.Heading");
    public static string ProfilesIntro => UiText.Get("Setup.Profiles.Intro");
    public static string ProfilesActiveBadge => UiText.Get("Setup.Profiles.ActiveBadge");
    public static string ProfilesArchivedBadge => UiText.Get("Setup.Profiles.ArchivedBadge");
    public static string ProfilesSwitch => UiText.Get("Setup.Profiles.Switch");
    public static string ProfilesArchive => UiText.Get("Setup.Profiles.Archive");
    public static string ProfilesRestore => UiText.Get("Setup.Profiles.Restore");
    public static string ProfilesShowArchived => UiText.Get("Setup.Profiles.ShowArchived");
    public static string ProfilesNewHeading => UiText.Get("Setup.Profiles.NewHeading");
    public static string ProfilesNameLabel => UiText.Get("Setup.Profiles.NameLabel");
    public static string ProfilesNamePlaceholder => UiText.Get("Setup.Profiles.NamePlaceholder");
    public static string ProfilesWipeLabel => UiText.Get("Setup.Profiles.WipeLabel");
    public static string ProfilesWipePlaceholder => UiText.Get("Setup.Profiles.WipePlaceholder");
    public static string ProfilesModeLabel => UiText.Get("Setup.Profiles.ModeLabel");
    public static string ProfilesCreate => UiText.Get("Setup.Profiles.Create");
    public static string ProfilesNone => UiText.Get("Setup.Profiles.None");
    public static string ProfilesModeUnset => UiText.Get("Setup.Profiles.ModeUnset");
    public static string ProfilesDataFor(object? arg0, object? arg1) => UiText.Format("Setup.Profiles.DataFor", arg0, arg1);
    public static string ProfilesNoProfile => UiText.Get("Setup.Profiles.NoProfile");
    public static string ProfilesUnknownMode => UiText.Get("Setup.Profiles.UnknownMode");
    public static string ProfilesCreated(object? arg0) => UiText.Format("Setup.Profiles.Created", arg0);
    public static string ProfilesSwitched(object? arg0) => UiText.Format("Setup.Profiles.Switched", arg0);
    public static string ProfilesArchivedDone(object? arg0) => UiText.Format("Setup.Profiles.ArchivedDone", arg0);
    public static string ProfilesRestored(object? arg0) => UiText.Format("Setup.Profiles.Restored", arg0);
    public static string ProfilesEdit => UiText.Get("Setup.Profiles.Edit");
    public static string ProfilesSaveEdit => UiText.Get("Setup.Profiles.SaveEdit");
    public static string ProfilesCancelEdit => UiText.Get("Setup.Profiles.CancelEdit");
    public static string ProfilesEdited(object? arg0) => UiText.Format("Setup.Profiles.Edited", arg0);
}
