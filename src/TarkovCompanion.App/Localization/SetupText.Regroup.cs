namespace TarkovCompanion.App.Localization;

/// <summary>[#902 P6] The group headings of the eight Setup sections, and the lines that moved with them.</summary>
public static partial class SetupText
{
    public static string ProgressLearnTitle => UiText.Get("Setup.Progress.LearnTitle");
    public static string ProgressLearnLine => UiText.Get("Setup.Progress.LearnLine");
    public static string ProgressMoveHeading => UiText.Get("Setup.Progress.MoveHeading");
    public static string ProgressMergeHeading => UiText.Get("Setup.Progress.MergeHeading");
    public static string CaptureShortcutLine => UiText.Get("Setup.GameCapture.ShortcutLine");
    public static string GameCaptureRecognitionHeading => UiText.Get("Setup.GameCapture.RecognitionHeading");
    public static string GameCaptureCleanupHeading => UiText.Get("Setup.GameCapture.CleanupHeading");
    public static string GameCaptureLootHeading => UiText.Get("Setup.GameCapture.LootHeading");
    public static string GameCaptureFoldersHeading => UiText.Get("Setup.GameCapture.FoldersHeading");
    public static string DataNetworkSquadHeading => UiText.Get("Setup.DataNetwork.SquadHeading");
    public static string DataNetworkKeptHeading => UiText.Get("Setup.DataNetwork.KeptHeading");
    public static string DataNetworkGameDataHeading => UiText.Get("Setup.DataNetwork.GameDataHeading");
    public static string AppearanceWindowHeading => UiText.Get("Setup.AppearanceWindow.WindowHeading");
    public static string UpdatesFeaturesHeading => UiText.Get("Setup.Updates.FeaturesHeading");
    public static string UpdatesDiagnosticsHeading => UiText.Get("Setup.Updates.DiagnosticsHeading");
    public static string AccessibilityCaptureRow => UiText.Get("Setup.Accessibility.CaptureRow");

    /// <summary>Local only's own state beside its switch: what is in force, not only what is stored.</summary>
    public static string NetworkLocalOnlyState(bool stored, bool forced, bool startedOffline) =>
        forced || stored ? UiText.Get("Setup.Network.LocalOnly.StateOn")
        : startedOffline ? UiText.Get("Setup.Network.LocalOnly.StateStartedOffline")
        : UiText.Get("Setup.Network.LocalOnly.StateOff");

    /// <summary>Home's network line, from the policy rather than a claim written once.</summary>
    public static string HomeNetworkTitle(bool localOnly, int allowed, int total) => localOnly
        ? UiText.Get("Setup.Home.Privacy.NetworkLocalOnly")
        : UiText.Format("Setup.Home.Privacy.NetworkOnline", allowed, total);

    public static string HomeNetworkDetail(bool localOnly, IReadOnlyList<string> allowed) => localOnly
        ? UiText.Get("Setup.Home.Privacy.NetworkLocalOnlyDetail")
        : allowed.Count == 0
            ? UiText.Get("Setup.Home.Privacy.NetworkNoneDetail")
            : UiText.Format("Setup.Home.Privacy.NetworkOnlineDetail", string.Join(", ", allowed));
}
