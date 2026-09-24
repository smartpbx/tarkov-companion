using System.Globalization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// The capture panel's own words that were literals rather than V2ShellText keys: picture intake,
/// the batch rows, the review's evidence line, "Read as…" and the retention chip (#314).
/// </summary>
/// <remarks>
/// A batch row's status is set in three places (the intake, the bridge and the panel) and shown
/// as it arrives, so each is a word from here rather than a code the panel maps.
/// </remarks>
public static partial class ShellText
{
    public static string CaptureManualHeading => UiText.Get("Shell.Capture.Manual.Heading");
    public static string CaptureManualHint => UiText.Get("Shell.Capture.Manual.Hint");
    public static string CapturePickPictures => UiText.Get("Shell.Capture.Manual.Pick");
    public static string CaptureCancelRemaining => UiText.Get("Shell.Capture.Manual.CancelRemaining");
    public static string CaptureCandidatesHeading => UiText.Get("Shell.Capture.Manual.CandidatesHeading");
    public static string CaptureChooseScreenshots => UiText.Get("Shell.Capture.Manual.ChooseScreenshots");
    public static string CaptureNoPicturesSelected => UiText.Get("Shell.Capture.Manual.NoneSelected");
    public static string CaptureCancelBatchFirst => UiText.Get("Shell.Capture.Manual.CancelBatchFirst");
    public static string CaptureNoFilePicker => UiText.Get("Shell.Capture.Manual.NoFilePicker");
    public static string CaptureCancellingRemaining => UiText.Get("Shell.Capture.Manual.CancellingRemaining");
    public static string CaptureClipboardUnreadable => UiText.Get("Shell.Capture.Manual.ClipboardUnreadable");
    public static string CaptureCannotReadHere => UiText.Get("Shell.Capture.Manual.CannotReadHere");
    public static string CaptureStillReading => UiText.Get("Shell.Capture.Manual.StillReading");
    public static string CaptureNoPicture => UiText.Get("Shell.Capture.Manual.NoPicture");
    public static string CapturePictureUnreadable => UiText.Get("Shell.Capture.Manual.PictureUnreadable");
    public static string CaptureNotAPictureFile => UiText.Get("Shell.Capture.Manual.NotAPictureFile");
    public static string CaptureFileGone => UiText.Get("Shell.Capture.Manual.FileGone");
    public static string CaptureAlreadyRead => UiText.Get("Shell.Capture.Manual.AlreadyRead");
    public static string CaptureBatchCancelled => UiText.Get("Shell.Capture.Manual.BatchCancelled");
    public static string CaptureBatchUnreadable => UiText.Get("Shell.Capture.Manual.BatchUnreadable");
    public static string CaptureHoldScreen => UiText.Get("Shell.Capture.Manual.HoldScreen");
    public static string CapturePicture(int number) => UiText.Format("Shell.Capture.Manual.Picture", number);
    public static string CaptureQueuedPictures(int count) => UiText.Plural("Shell.Capture.Manual.QueuedPictures", count);
    public static string CaptureBatchOutcome(int accepted, int failed, int cancelled) =>
        UiText.Format("Shell.Capture.Manual.BatchOutcome", accepted, failed, cancelled);
    public static string CaptureReadingPicture(int width, int height) => UiText.Format("Shell.Capture.Manual.ReadingPicture", width, height);
    public static string CaptureNotTakenIn(object? code) => UiText.Format("Shell.Capture.Manual.NotTakenIn", code);
    public static string CaptureIntentNotSupported(string intent) => UiText.Format("Shell.Capture.IntentNotSupported", intent);
    public static string CaptureIntentNotSupportedTag(string intent) => UiText.Format("Shell.Capture.IntentNotSupportedTag", intent);

    public static string CaptureRowQueued => UiText.Get("Shell.Capture.Row.Queued");
    public static string CaptureRowReading => UiText.Get("Shell.Capture.Row.Reading");
    public static string CaptureRowCancelling => UiText.Get("Shell.Capture.Row.Cancelling");
    public static string CaptureRowCancelled => UiText.Get("Shell.Capture.Row.Cancelled");
    public static string CaptureRowDone => UiText.Get("Shell.Capture.Row.Done");
    public static string CaptureRowNoChange => UiText.Get("Shell.Capture.Row.NoChange");
    public static string CaptureRowRetryRequested => UiText.Get("Shell.Capture.Row.RetryRequested");
    public static string CaptureRowNeedsReview => UiText.Get("Shell.Capture.Row.NeedsReview");
    public static string CaptureRowNotCompleted => UiText.Get("Shell.Capture.Row.NotCompleted");
    public static string CaptureRowUnavailable => UiText.Get("Shell.Capture.Row.Unavailable");
    public static string CaptureRowUnreadable => UiText.Get("Shell.Capture.Row.Unreadable");
    public static string CaptureRowNotAPicture => UiText.Get("Shell.Capture.Row.NotAPicture");
    public static string CaptureRowFileGone => UiText.Get("Shell.Capture.Row.FileGone");
    public static string CaptureRowNotQueuedLimit(int limit) => UiText.Format("Shell.Capture.Row.NotQueuedLimit", limit);
    public static string CaptureRowNotQueued(object? code) => UiText.Format("Shell.Capture.Row.NotQueued", code);

    public static string CaptureReviewFleaOffers => UiText.Get("Shell.Capture.Review.FleaOffers");
    public static string CaptureReviewScreenshot => UiText.Get("Shell.Capture.Review.Screenshot");
    public static string CaptureReviewScreenshotFleaRows => UiText.Get("Shell.Capture.Review.ScreenshotFleaRows");
    public static string CaptureReviewScreenshotCode(object? code) => UiText.Format("Shell.Capture.Review.ScreenshotCode", code);
    public static string CaptureReviewLootScan => UiText.Get("Shell.Capture.Review.LootScan");
    public static string CaptureReviewIdentified(string name, string percent) => UiText.Format("Shell.Capture.Review.Identified", name, percent);
    public static string CaptureReviewAlso(string names) => UiText.Format("Shell.Capture.Review.Also", names);

    public static string ReadAsMenu => UiText.Get("Shell.ReadAs.Menu");
    public static string ReadAsHeading(string intent) => UiText.Format("Shell.ReadAs.Heading", intent);
    public static string ReadAsImageReleased => UiText.Get("Shell.ReadAs.ImageReleased");
    public static string ReadAsByYou(string to, string from) => UiText.Format("Shell.ReadAs.ByYou", to, from);
    public static string ReadAsCannotHere => UiText.Get("Shell.ReadAs.CannotHere");
    public static string ReadAsFailed(object? code) => UiText.Format("Shell.ReadAs.Failed", code);
    public static string ReadAsReadingAgain(string intent) => UiText.Format("Shell.ReadAs.ReadingAgain", intent);

    /// <summary>The short name "Read as…" gives an intent; every <see cref="ScanIntent"/> has one.</summary>
    public static string ReadAsIntent(ScanIntent intent) => intent switch
    {
        ScanIntent.Loot => UiText.Get("Shell.ReadAs.Intent.Loot"),
        ScanIntent.Stash => UiText.Get("Shell.ReadAs.Intent.Stash"),
        ScanIntent.Flea => UiText.Get("Shell.ReadAs.Intent.Flea"),
        ScanIntent.QuestItems => UiText.Get("Shell.ReadAs.Intent.QuestItems"),
        ScanIntent.Ammo => UiText.Get("Shell.ReadAs.Intent.Ammo"),
        ScanIntent.Keys => UiText.Get("Shell.ReadAs.Intent.Keys"),
        ScanIntent.ExtractsAndMap => UiText.Get("Shell.ReadAs.Intent.ExtractsAndMap"),
        ScanIntent.HealthAndCharacter => UiText.Get("Shell.ReadAs.Intent.HealthAndCharacter"),
        _ => UiText.Get("Shell.ReadAs.Intent.Auto"),
    };

    public static string RetentionNotKept => UiText.Get("Shell.Retention.NotKept");
    public static string RetentionTidied(int hours) => UiText.Format("Shell.Retention.Tidied", hours);
    public static string RetentionStays => UiText.Get("Shell.Retention.Stays");
    public static string RetentionNoCopy => UiText.Get("Shell.Retention.NoCopy");
    public static string RetentionRecycled(int hours) => UiText.Format("Shell.Retention.Recycled", hours);
    public static string RetentionStaysDetail => UiText.Get("Shell.Retention.StaysDetail");
    public static string RetentionHeld(string minutes) => UiText.Format("Shell.Retention.Held", minutes);
    public static string RetentionNeedsNewCapture => UiText.Get("Shell.Retention.NeedsNewCapture");

    public static string FaultRetry => UiText.Get("Shell.Fault.Retry");
    public static string FaultRetrying => UiText.Get("Shell.Fault.Retrying");
    public static string FaultRestWorks(int count) => UiText.Plural("Shell.Fault.RestWorks", count);
    public static string FaultDidNotLoad(string names) => UiText.Format("Shell.Fault.DidNotLoad", names);
    public static string FaultListTwo(string first, string second) => UiText.Format("Shell.Fault.ListTwo", first, second);
    public static string FaultListMany(string leading, string last) => UiText.Format("Shell.Fault.ListMany", leading, last);
}
