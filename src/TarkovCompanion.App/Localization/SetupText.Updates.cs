namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string UpdatesCheckLabel => UiText.Get("Setup.Updates.CheckLabel");
    public static string UpdatesUpdateNowLabel => UiText.Get("Setup.Updates.UpdateNowLabel");
    public static string UpdatesInstallerLabel => UiText.Get("Setup.Updates.InstallerLabel");
    public static string UpdatesChannelLabel => UiText.Get("Setup.Updates.ChannelLabel");
    public static string UpdatesInstalledLabel => UiText.Get("Setup.Updates.InstalledLabel");
    public static string UpdatesAvailableLabel => UiText.Get("Setup.Updates.AvailableLabel");
    public static string UpdatesNotesHeading => UiText.Get("Setup.Updates.NotesHeading");
    public static string UpdatesRetryLabel => UiText.Get("Setup.Updates.RetryLabel");
    public static string UpdatesGoingBackHeading => UiText.Get("Setup.Updates.GoingBackHeading");
    public static string UpdatesGoingBack => UiText.Get("Setup.Updates.GoingBack");
    public static string RollbackStartLabel => UiText.Get("Setup.Rollback.StartLabel");
    public static string RollbackCancelLabel => UiText.Get("Setup.Rollback.CancelLabel");
    public static string RollbackResumeLabel => UiText.Get("Setup.Rollback.ResumeLabel");
    public static string RollbackProvenanceHeading => UiText.Get("Setup.Rollback.ProvenanceHeading");
    public static string RollbackConfirmHeading(object? installed, object? target) => UiText.Format("Setup.Rollback.ConfirmHeading", installed, target);
    public static string RollbackStepDownload(object? version) => UiText.Format("Setup.Rollback.StepDownload", version);
    public static string RollbackStepLocalCopy(object? version) => UiText.Format("Setup.Rollback.StepLocalCopy", version);
    public static string RollbackStepInstall(object? version) => UiText.Format("Setup.Rollback.StepInstall", version);
    public static string RollbackStepDataStays => UiText.Get("Setup.Rollback.StepDataStays");
    public static string RollbackStepPin(object? version, object? holdThrough) => UiText.Format("Setup.Rollback.StepPin", version, holdThrough);
    public static string RollbackConfirmLabel(object? version) => UiText.Format("Setup.Rollback.ConfirmLabel", version);
    public static string RollbackPinText(object? version, object? holdThrough) => UiText.Format("Setup.Rollback.PinText", version, holdThrough);
    public static string RollbackLooking => UiText.Get("Setup.Rollback.Looking");
    public static string RollbackNoOlder => UiText.Get("Setup.Rollback.NoOlder");
    public static string RollbackLookFailed(object? reason) => UiText.Format("Setup.Rollback.LookFailed", reason);
    public static string RollbackFetching(object? version) => UiText.Format("Setup.Rollback.Fetching", version);
    public static string RollbackInstalling(object? version) => UiText.Format("Setup.Rollback.Installing", version);
    public static string RollbackStartFailed(object? reason) => UiText.Format("Setup.Rollback.StartFailed", reason);
}
