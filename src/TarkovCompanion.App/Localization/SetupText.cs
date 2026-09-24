namespace TarkovCompanion.App.Localization;

/// <summary>Every word the Setup workspace shows, typed, from Localization/Strings (#314).</summary>
/// <remarks>
/// One partial per part of the page: Home (Overview), Profiles and Progress, Data, Privacy,
/// Appearance (with Accessibility and Displays), Updates, Notifications, Diagnostics with the
/// self-test probes, About with Data &amp; Privacy, Settings (the V1 settings and quest-exchange
/// lines Setup binds) and Language. Members carry their part's name as a prefix. A key picked at
/// run time (a section tab, a theme, a Home step) keeps its old V2ShellText name in code and is
/// read from here through <see cref="Moved"/>, so it has no member of its own.
/// </remarks>
public static partial class SetupText
{
    public static string SectionsRegion => UiText.Get("Setup.Sections.Region");
    public static string NotificationsIntro => UiText.Get("Setup.Notifications.Intro");
    public static string NotificationsRaidNote => UiText.Get("Setup.Notifications.RaidNote");
    public static string NotificationsTestLabel => UiText.Get("Setup.Notifications.TestLabel");
    public static string GameProfileScreenshotFolderLabel => UiText.Get("Setup.GameProfile.ScreenshotFolderLabel");
    public static string GameProfileLogFolderLabel => UiText.Get("Setup.GameProfile.LogFolderLabel");
    public static string GameProfileSaveLabel => UiText.Get("Setup.GameProfile.SaveLabel");
    public static string RecognitionRuntimeWarning => UiText.Get("Setup.Recognition.RuntimeWarning");
    public static string TeamDevicesOpenLabel => UiText.Get("Setup.TeamDevices.OpenLabel");
    public static string GameProfileFolderPlaceholder => UiText.Get("Setup.GameProfile.FolderPlaceholder");
    public static string RecognitionScanHint => UiText.Get("Setup.Recognition.ScanHint");
    public static string DiagnosticsLogLabel => UiText.Get("Setup.Diagnostics.LogLabel");
    public static string DiagnosticsRelayNote => UiText.Get("Setup.Diagnostics.RelayNote");
    public static string ReportReview => UiText.Get("Setup.Report.Review");
    public static string ReportSend => UiText.Get("Setup.Report.Send");
    public static string ReportDiscard => UiText.Get("Setup.Report.Discard");
    public static string ReportHeading => UiText.Get("Setup.Report.Heading");
    public static string ReportNote => UiText.Get("Setup.Report.Note");
    public static string ReportSize(object? arg0) => UiText.Format("Setup.Report.Size", arg0);
    public static string ReportNothing => UiText.Get("Setup.Report.Nothing");
    public static string ReportSending => UiText.Get("Setup.Report.Sending");
    public static string ReportFailed(object? arg0) => UiText.Format("Setup.Report.Failed", arg0);
    public static string ReportDestination => UiText.Get("Setup.Report.Destination");
    public static string ReportConsent => UiText.Get("Setup.Report.Consent");
    public static string ReportNeedsConsent => UiText.Get("Setup.Report.NeedsConsent");
    public static string InfoMore => UiText.Get("Setup.Info.More");
    public static string InfoLess => UiText.Get("Setup.Info.Less");
    public static string InfoOpenPrivacy => UiText.Get("Setup.Info.OpenPrivacy");
    public static string InfoOpenSharing => UiText.Get("Setup.Info.OpenSharing");
    public static string PathsShow => UiText.Get("Setup.Paths.Show");
    public static string PathsHide => UiText.Get("Setup.Paths.Hide");
    public static string PathsHiddenNote => UiText.Get("Setup.Paths.HiddenNote");
    public static string PathsRevealedNote => UiText.Get("Setup.Paths.RevealedNote");
    public static string DiagnosticsSelfTestHeading => UiText.Get("Setup.Diagnostics.SelfTestHeading");
    public static string DiagnosticsSelfTestIntro => UiText.Get("Setup.Diagnostics.SelfTestIntro");
    public static string DiagnosticsSelfTestRun => UiText.Get("Setup.Diagnostics.SelfTestRun");
    public static string DiagnosticsSelfTestStop => UiText.Get("Setup.Diagnostics.SelfTestStop");
    public static string DiagnosticsSelfTestCopy => UiText.Get("Setup.Diagnostics.SelfTestCopy");
    public static string DiagnosticsCopyLabel => UiText.Get("Setup.Diagnostics.CopyLabel");
    public static string DiagnosticsReportLabel => UiText.Get("Setup.Diagnostics.ReportLabel");
}
