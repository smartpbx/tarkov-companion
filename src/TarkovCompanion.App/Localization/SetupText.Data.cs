namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string DataSyncLabel => UiText.Get("Setup.Data.SyncLabel");
    public static string DataOfflineNote => UiText.Get("Setup.Data.OfflineNote");
    public static string DataSourceLabel => UiText.Get("Setup.Data.SourceLabel");
    public static string DataSource(object? arg0) => UiText.Format("Setup.Data.Source", arg0);
    public static string DataCoverageLabel => UiText.Get("Setup.Data.CoverageLabel");
    public static string DataCoverage(object? arg0, object? arg1, object? arg2) => UiText.Format("Setup.Data.Coverage", arg0, arg1, arg2);
    public static string DataNoData => UiText.Get("Setup.Data.NoData");
    public static string DataLastAttemptLabel => UiText.Get("Setup.Data.LastAttemptLabel");
    public static string DataLastSuccessLabel => UiText.Get("Setup.Data.LastSuccessLabel");
    public static string DataNextLabel => UiText.Get("Setup.Data.NextLabel");
    public static string DataNever => UiText.Get("Setup.Data.Never");
    public static string DataAttemptNone => UiText.Get("Setup.Data.AttemptNone");
    public static string DataAttemptOffline => UiText.Get("Setup.Data.AttemptOffline");
    public static string DataAttemptRunning => UiText.Get("Setup.Data.AttemptRunning");
    public static string DataAttemptSucceeded(object? arg0) => UiText.Format("Setup.Data.AttemptSucceeded", arg0);
    public static string DataAttemptFailed(object? arg0) => UiText.Format("Setup.Data.AttemptFailed", arg0);
    public static string DataAttemptTimedOut(object? arg0) => UiText.Format("Setup.Data.AttemptTimedOut", arg0);
    public static string DataAttemptStopped(object? arg0) => UiText.Format("Setup.Data.AttemptStopped", arg0);
    public static string DataNextDemo => UiText.Get("Setup.Data.NextDemo");
    public static string DataNextOffline => UiText.Get("Setup.Data.NextOffline");
    public static string DataNextAtLaunch(object? arg0) => UiText.Format("Setup.Data.NextAtLaunch", arg0);
    public static string DataReasonLabel => UiText.Get("Setup.Data.ReasonLabel");
    public static string DataRetryLabel => UiText.Get("Setup.Data.RetryLabel");
    public static string DataDatabaseHeading => UiText.Get("Setup.Data.DatabaseHeading");
    public static string DataVersionLine(object? arg0, object? arg1) => UiText.Format("Setup.Data.VersionLine", arg0, arg1);
    public static string DataNoMigrations => UiText.Get("Setup.Data.NoMigrations");
    public static string DataUnknownTime => UiText.Get("Setup.Data.UnknownTime");
    public static string DataBackupLine(object? arg0, object? arg1) => UiText.Format("Setup.Data.BackupLine", arg0, arg1);
    public static string DataNoBackup => UiText.Get("Setup.Data.NoBackup");
    public static string DataBackUpNowLabel => UiText.Get("Setup.Data.BackUpNowLabel");
    public static string DataOpenBackupFolderLabel => UiText.Get("Setup.Data.OpenBackupFolderLabel");
    public static string DataBackingUp => UiText.Get("Setup.Data.BackingUp");
    public static string DataBackedUp => UiText.Get("Setup.Data.BackedUp");
    public static string DataBackupFailed(object? arg0) => UiText.Format("Setup.Data.BackupFailed", arg0);
    public static string HorizonHeading => UiText.Get("Setup.Horizon.Heading");
    public static string HorizonHint => UiText.Get("Setup.Horizon.Hint");
    public static string HorizonQuestLabel => UiText.Get("Setup.Horizon.QuestLabel");
    public static string HorizonHideoutLabel => UiText.Get("Setup.Horizon.HideoutLabel");
    public static string HorizonNextOnly => UiText.Get("Setup.Horizon.NextOnly");
    public static string HorizonNextThree => UiText.Get("Setup.Horizon.NextThree");
    public static string HorizonNextFive => UiText.Get("Setup.Horizon.NextFive");
    public static string HorizonAll => UiText.Get("Setup.Horizon.All");
}
