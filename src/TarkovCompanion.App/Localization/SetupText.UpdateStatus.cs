namespace TarkovCompanion.App.Localization;

/// <summary>
/// Setup › Updates' status lines from the update gateway and rollback, the pending-update notice,
/// the data folder line and the provenance labels (#314).
/// </summary>
/// <remarks>
/// A channel's name and a feed host stay as they are: they are written into the provenance
/// record, and a record must read the same whatever language wrote it.
/// </remarks>
public static partial class SetupText
{
    public static string UpdateInstalledVersion(object? version) => UiText.Format("Setup.UpdateStatus.InstalledVersion", version);
    public static string UpdateRunningFromFolder(object? version) => UiText.Format("Setup.UpdateStatus.RunningFromFolder", version);
    public static string UpdateCannotUpdateFolder => UiText.Get("Setup.UpdateStatus.CannotUpdateFolder");
    public static string UpdateCouldNotCheck(object? detail) => UiText.Format("Setup.UpdateStatus.CouldNotCheck", detail);
    public static string UpdateUpToDate => UiText.Get("Setup.UpdateStatus.UpToDate");
    public static string UpdateAvailable(object? version) => UiText.Format("Setup.UpdateStatus.Available", version);
    public static string UpdateCheckFirst => UiText.Get("Setup.UpdateStatus.CheckFirst");
    public static string UpdateDownloadAgain => UiText.Get("Setup.UpdateStatus.DownloadAgain");
    public static string UpdateReady(object? version) => UiText.Format("Setup.UpdateStatus.Ready", version);
    public static string UpdateRefused => UiText.Get("Setup.UpdateStatus.Refused");
    public static string UpdateCouldNotDownload(object? detail) => UiText.Format("Setup.UpdateStatus.CouldNotDownload", detail);
    public static string UpdateCannotGoBackFolder => UiText.Get("Setup.UpdateStatus.CannotGoBackFolder");
    public static string UpdateNoOlderFeedUnread(object? detail) => UiText.Format("Setup.UpdateStatus.NoOlderFeedUnread", detail);
    public static string UpdateNoOlder => UiText.Get("Setup.UpdateStatus.NoOlder");
    public static string UpdateNothingToGoBackTo => UiText.Get("Setup.UpdateStatus.NothingToGoBackTo");
    public static string UpdateWouldNotGoBack(object? version) => UiText.Format("Setup.UpdateStatus.WouldNotGoBack", version);
    public static string UpdateRollbackRefused => UiText.Get("Setup.UpdateStatus.RollbackRefused");
    public static string UpdateCouldNotFetch(object? version, object? detail) => UiText.Format("Setup.UpdateStatus.CouldNotFetch", version, detail);
    public static string UpdateStayingOn(object? version, object? held) => UiText.Format("Setup.UpdateStatus.StayingOn", version, held);
    public static string UpdateDidNotApply(object? version) => UiText.Format("Setup.UpdateStatus.DidNotApply", version);
    public static string UpdateDidNotApplyBecause(object? version, object? reason) => UiText.Format("Setup.UpdateStatus.DidNotApplyBecause", version, reason);
    public static string UpdateApplyNow => UiText.Get("Setup.UpdateStatus.ApplyNow");
    public static string UpdateRunInstaller => UiText.Get("Setup.UpdateStatus.RunInstaller");
    public static string UpdateAdvice => UiText.Get("Setup.UpdateStatus.Advice");
    public static string UpdateDataBesideBuild(object? root) => UiText.Format("Setup.UpdateStatus.DataBesideBuild", root);
    public static string UpdateDataKept(object? root) => UiText.Format("Setup.UpdateStatus.DataKept", root);
    public static string ProvenanceNotRecordedInstalled => UiText.Get("Setup.Provenance.NotRecordedInstalled");
    public static string ProvenanceNotRecorded => UiText.Get("Setup.Provenance.NotRecorded");
    public static string ProvenanceAsFeedLists(object? sha) => UiText.Format("Setup.Provenance.AsFeedLists", sha);
    public static string ProvenanceWentBack(object? when) => UiText.Format("Setup.Provenance.WentBack", when);
    public static string ProvenanceVersion => UiText.Get("Setup.Provenance.Version");
    public static string ProvenanceChannel => UiText.Get("Setup.Provenance.Channel");
    public static string ProvenanceFeed => UiText.Get("Setup.Provenance.Feed");
    public static string ProvenanceSha => UiText.Get("Setup.Provenance.Sha");
    public static string ProvenanceApplied => UiText.Get("Setup.Provenance.Applied");
}
