namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string PrivacyScreenshotIntro => UiText.Get("Setup.Privacy.ScreenshotIntro");
    public static string PrivacyRetentionLabel => UiText.Get("Setup.Privacy.RetentionLabel");
    public static string PrivacyRetentionValueLabel => UiText.Get("Setup.Privacy.RetentionValueLabel");
    public static string PrivacyRecycleNote => UiText.Get("Setup.Privacy.RecycleNote");
    public static string CleanupPreview => UiText.Get("Setup.Cleanup.Preview");
    public static string CleanupStart => UiText.Get("Setup.Cleanup.Start");
    public static string CleanupStop => UiText.Get("Setup.Cleanup.Stop");
    public static string CleanupConfirm => UiText.Get("Setup.Cleanup.Confirm");
    public static string CleanupCancel => UiText.Get("Setup.Cleanup.Cancel");
    public static string CleanupFolder => UiText.Get("Setup.Cleanup.Folder");
    public static string CleanupPolicy(object? arg0) => UiText.Format("Setup.Cleanup.Policy", arg0);
    public static string CleanupSummary(object? arg0, object? arg1) => UiText.Format("Setup.Cleanup.Summary", arg0, arg1);
    public static string CleanupSummaryOne(object? arg0) => UiText.Format("Setup.Cleanup.SummaryOne", arg0);
    public static string CleanupSummaryNone => UiText.Get("Setup.Cleanup.SummaryNone");
    public static string CleanupMoreFiles(object? arg0) => UiText.Format("Setup.Cleanup.MoreFiles", arg0);
    public static string CleanupExcluded(object? arg0) => UiText.Format("Setup.Cleanup.Excluded", arg0);
    public static string CleanupExOther(object? arg0) => UiText.Format("Setup.Cleanup.ExOther", arg0);
    public static string CleanupLedgerHeading => UiText.Get("Setup.Cleanup.LedgerHeading");
    public static string CleanupLedgerEmpty => UiText.Get("Setup.Cleanup.LedgerEmpty");
    public static string CleanupLedgerEntry(object? arg0, object? arg1, object? arg2, object? arg3) => UiText.Format("Setup.Cleanup.LedgerEntry", arg0, arg1, arg2, arg3);
    public static string CleanupFailureLine(object? count, object? reason) => UiText.Format("Setup.Cleanup.FailureLine", count, reason);
    public static string CleanupBytes(long bytes) => UiText.Format("Setup.Cleanup.Bytes", bytes);
    public static string CleanupKilobytes(double kilobytes) => UiText.Format("Setup.Cleanup.Kilobytes", kilobytes);
    public static string CleanupMegabytes(double megabytes) => UiText.Format("Setup.Cleanup.Megabytes", megabytes);
    public static string CleanupGigabytes(double gigabytes) => UiText.Format("Setup.Cleanup.Gigabytes", gigabytes);
}
