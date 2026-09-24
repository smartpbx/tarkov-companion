using System.Globalization;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Every word the provisional shell draws, by key.
/// </summary>
/// <remarks>
/// Kept out of the views so the views carry no literal copy, the same rule the #266 gallery holds
/// itself to, and so a label #265 moves is one line here rather than a search through markup.
/// English only until #269 supplies culture resources; a message is formatted whole with an
/// explicit culture rather than assembled from fragments.
///
/// Short on purpose. A label is a line: scripts/sweep-prose.sh fails a label past 120 characters,
/// and standing policy prose belongs in Setup and docs, not in chrome that is on every page.
/// </remarks>
public static class V2ShellText
{
    public static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["V2.Shell.Provisional"] = "provisional - #265 not yet run",
        ["V2.Shell.Variant.A"] = "Variant A · workspace rail",
        ["V2.Shell.Variant.B"] = "Variant B · workflow hub",

        ["V2.Shell.Palette.Heading"] = "Commands",
        ["V2.Shell.Palette.Query"] = "Type a command or paste an address",
        ["V2.Shell.Palette.Recents"] = "Recent",
        ["V2.Shell.Palette.Pins"] = "Pinned",
        ["V2.Shell.Palette.OpenAddress"] = "Open {0}",
        ["V2.Shell.Palette.Empty"] = "No command matches",

        ["V2.Shell.Continue.Heading"] = "Continue",
        ["V2.Shell.Continue.Empty"] = "Nothing to continue yet",

        ["V2.Shell.StateGlyph.Ready"] = "✓",
        ["V2.Shell.StateGlyph.Loading"] = "…",
        ["V2.Shell.StateGlyph.Empty"] = "○",
        ["V2.Shell.StateGlyph.Offline"] = "⊘",
        ["V2.Shell.StateGlyph.Stale"] = "◷",
        ["V2.Shell.StateGlyph.Partial"] = "◔",
        ["V2.Shell.StateGlyph.Denied"] = "⌕",
        ["V2.Shell.StateGlyph.Failed"] = "×",

        ["V2.Shell.Capture.Heading"] = "Capture",
        ["V2.Shell.Capture.NotArmed"] = "Not armed: screenshots use Auto-detect",
        ["V2.Shell.Capture.ArmedHeader"] = "Armed: {0} · rev {1}",
        ["V2.Shell.Capture.Armed"] = "Armed: {0} · rev {1} · set on {2}",
        ["V2.Shell.Capture.NeedsDecision"] = "1 capture needs a decision",
        ["V2.Shell.Capture.Guidance"] = "Press the game's own screenshot key. The companion reads the saved file.",
        ["V2.Shell.Capture.Watching"] = "Screenshot folder is watched",
        ["V2.Shell.Capture.NotWatching"] = "Screenshot folder is not watched",
        ["V2.Shell.Capture.IntentHeading"] = "Intent",
        ["V2.Shell.Capture.Arm"] = "Arm selected intent",
        ["V2.Shell.Capture.ProgressHeading"] = "Progress",
        ["V2.Shell.Capture.PriorHeading"] = "Last capture",
        ["V2.Shell.Capture.Prior"] = "Last result: {0}",
        ["V2.Shell.Capture.NoPrior"] = "No capture yet",
        ["V2.Shell.Capture.NoReference"] = "No active capture reference",
        ["V2.Shell.Capture.ShortcutOn"] = "Window shortcut on",
        ["V2.Shell.Capture.ShortcutOff"] = "Window shortcut off",
        ["V2.Shell.Capture.Review"] = "Review",
        ["V2.Shell.Capture.Details"] = "Details",
        ["V2.Shell.Capture.Reference"] = "Reference {0}",
        ["V2.Shell.Capture.Evidence"] = "{0} · {1}",
        ["V2.Shell.Capture.Artifact"] = "Capture {0} · {1}",
        ["V2.Shell.Capture.Session"] = "Capture session",
        ["V2.Shell.Capture.Percent"] = "{0}%",

        ["V2.Shell.Capture.Attention.IntentMismatch"] = "Capture {0}: detected {1}, not the armed intent",
        ["V2.Shell.Capture.Attention.UnknownContext"] = "Couldn't tell what capture {0} shows",
        ["V2.Shell.Capture.Attention.StillWriting"] = "Capture {0} is still being written",
        ["V2.Shell.Capture.Attention.Duplicate"] = "Capture {0} matches an earlier capture",
        ["V2.Shell.Capture.Attention.DeviceRace"] = "Capture intent changed on another device",
        ["V2.Shell.Capture.Attention.SourceUnavailable"] = "Capture {0} is no longer available",
        ["V2.Shell.Capture.AttentionDetail.IntentMismatch"] = "Nothing changed. Choose how this screenshot should be analysed.",
        ["V2.Shell.Capture.AttentionDetail.UnknownContext"] = "Nothing changed. Choose a context or skip this screenshot.",
        ["V2.Shell.Capture.AttentionDetail.StillWriting"] = "The file did not settle before checking stopped.",
        ["V2.Shell.Capture.AttentionDetail.Duplicate"] = "No second result was created.",
        ["V2.Shell.Capture.AttentionDetail.DeviceRace"] = "Your change was not applied because a newer intent revision arrived first.",
        ["V2.Shell.Capture.AttentionDetail.SourceUnavailable"] = "The source cannot be read again. Nothing was changed.",

        ["V2.Shell.Capture.Action.Skip"] = "Skip this screenshot",
        ["V2.Shell.Capture.Action.AnalyzeAsArmed"] = "Analyse as armed",
        ["V2.Shell.Capture.Action.AnalyzeAsDetected"] = "Analyse as detected",
        ["V2.Shell.Capture.Action.AnalyzeAsSelected"] = "Analyse as selected",
        ["V2.Shell.Capture.Action.Retry"] = "Retry",
        ["V2.Shell.Capture.Action.AnalyzeAgain"] = "Analyse again",
        ["V2.Shell.Capture.Action.KeepCurrentIntent"] = "Keep current intent",
        ["V2.Shell.Capture.Action.ArmSelectedIntent"] = "Arm selected intent now",
        ["V2.Shell.Capture.Action.Review"] = "Review result",
        ["V2.Shell.Capture.Action.Correct"] = "Correct result",

        ["V2.Shell.Capture.Stage.Armed"] = "Armed",
        ["V2.Shell.Capture.Stage.AwaitingCapture"] = "Waiting",
        ["V2.Shell.Capture.Stage.Settling"] = "Settling",
        ["V2.Shell.Capture.Stage.Decoding"] = "Decoding",
        ["V2.Shell.Capture.Stage.DetectingContext"] = "Detecting",
        ["V2.Shell.Capture.Stage.DetectingRegions"] = "Finding regions",
        ["V2.Shell.Capture.Stage.Matching"] = "Matching",
        ["V2.Shell.Capture.Stage.EnrichingProfile"] = "Adding context",
        ["V2.Shell.Capture.Stage.Recommending"] = "Recommending",
        ["V2.Shell.Capture.Stage.AwaitingReview"] = "Review",
        ["V2.Shell.Capture.Stage.Complete"] = "Complete",
        ["V2.Shell.Capture.Stage.Cancelled"] = "Cancelled",
        ["V2.Shell.Capture.Stage.Failed"] = "Failed",
        ["V2.Shell.Capture.StageDetail.Armed"] = "Intent recorded",
        ["V2.Shell.Capture.StageDetail.AwaitingCapture"] = "Waiting for the next user-created screenshot",
        ["V2.Shell.Capture.StageDetail.Settling"] = "Waiting for the saved file to stop changing",
        ["V2.Shell.Capture.StageDetail.Decoding"] = "Reading visible pixels",
        ["V2.Shell.Capture.StageDetail.DetectingContext"] = "Identifying what the screenshot shows",
        ["V2.Shell.Capture.StageDetail.DetectingRegions"] = "Finding relevant visible regions",
        ["V2.Shell.Capture.StageDetail.Matching"] = "Matching visible facts to local data",
        ["V2.Shell.Capture.StageDetail.EnrichingProfile"] = "Applying profile and plan context",
        ["V2.Shell.Capture.StageDetail.Recommending"] = "Preparing the contextual result",
        ["V2.Shell.Capture.StageDetail.AwaitingReview"] = "Waiting for review",
        ["V2.Shell.Capture.StageDetail.Complete"] = "Capture completed",
        ["V2.Shell.Capture.StageDetail.Cancelled"] = "Capture cancelled",
        ["V2.Shell.Capture.StageDetail.Failed"] = "Capture failed",

        ["V2.Shell.Intent.Auto"] = "Auto-detect",
        ["V2.Shell.Intent.Loot"] = "Loot decision",
        ["V2.Shell.Intent.Stash"] = "Full stash",
        ["V2.Shell.Intent.Ammo"] = "Ammo",
        ["V2.Shell.Intent.Keys"] = "Keys",
        ["V2.Shell.Intent.QuestItems"] = "Quest items",
        ["V2.Shell.Intent.ExtractsAndMap"] = "Map and extracts",
        ["V2.Shell.Intent.HealthAndCharacter"] = "Health and character",
        ["V2.Shell.Intent.Flea"] = "Flea listings you opened",

        ["V2.Shell.Capture.Context.Item"] = "Item",
        ["V2.Shell.Capture.Context.Grid"] = "Grid",
        ["V2.Shell.Capture.Context.Loot"] = "Loot",
        ["V2.Shell.Capture.Context.Stash"] = "Full stash",
        ["V2.Shell.Capture.Context.Ammo"] = "Ammo",
        ["V2.Shell.Capture.Context.Keys"] = "Keys",
        ["V2.Shell.Capture.Context.QuestItems"] = "Quest items",
        ["V2.Shell.Capture.Context.ExtractsAndMap"] = "Map and extracts",
        ["V2.Shell.Capture.Context.HealthAndCharacter"] = "Health and character",
        ["V2.Shell.Capture.Context.Flea"] = "Flea listings",

        ["V2.Shell.Suggestions.Heading"] = "Suggested",
        ["V2.Shell.Suggestions.Browse"] = "Browse by category",
        ["V2.Shell.Suggestions.Empty"] = "No suggestions match this filter",
        ["V2.Shell.Suggestions.Filter.All"] = "All",
        ["V2.Shell.Suggestions.Filter.Recent"] = "Recent",
        ["V2.Shell.Suggestions.Filter.Pinned"] = "Pinned",
        ["V2.Shell.Suggestions.Filter.Planned"] = "Current plan",
        ["V2.Shell.Suggestions.Source.Recent"] = "Opened recently",
        ["V2.Shell.Suggestions.Source.Pinned"] = "Pinned by you",
        ["V2.Shell.Suggestions.Source.Planned"] = "In {0} · {1}",
        ["V2.Shell.Suggestions.Item"] = "{0} · {1}",
        ["V2.Shell.Suggestions.Category.Planned"] = "Plan item",
        ["V2.Shell.Suggestions.Category.Items"] = "Items",
        ["V2.Shell.Suggestions.Category.ItemsDetail"] = "Browse the item catalog",
        ["V2.Shell.Suggestions.Category.Ammo"] = "Ammo",
        ["V2.Shell.Suggestions.Category.AmmoDetail"] = "Browse ammunition by caliber",
        ["V2.Shell.Suggestions.Category.Keys"] = "Keys",
        ["V2.Shell.Suggestions.Category.KeysDetail"] = "Browse keys and locks",
        ["V2.Shell.Suggestions.Category.Flea"] = "Flea",
        ["V2.Shell.Suggestions.Category.FleaDetail"] = "Browse local price facts",

        ["V2.Shell.Persistence.RetrySave"] = "Retry save",
        ["V2.Shell.Persistence.RetryReset"] = "Retry reset",
        ["V2.Shell.Persistence.Retrying"] = "Trying again…",
        ["V2.Shell.Persistence.SaveFailed"] = "Preview changes were not saved: {0}",
        ["V2.Shell.Persistence.ResetFailed"] = "Preview reset was not saved: {0}",
        ["V2.Shell.Persistence.UnknownFailure"] = "unknown storage failure",

        ["V2.Shell.Announce.Copied"] = "Address copied",
        ["V2.Shell.Announce.CopyFailed"] = "The address could not be copied",
        ["V2.Shell.Announce.PreviewReset"] = "This preview's navigation was reset",
        ["V2.Shell.Announce.PreviewCorrupt"] = "Saved preview navigation could not be read, so the preview started clean",
        ["V2.Shell.Announce.ShortcutOn"] = "Capture shortcut on",
        ["V2.Shell.Announce.ShortcutOff"] = "Capture shortcut off",
        ["V2.Shell.Announce.Pinned"] = "Pinned",
        ["V2.Shell.Announce.Unpinned"] = "Unpinned",
        ["V2.Shell.Announce.SyncStarted"] = "Sync started",
        ["V2.Shell.Announce.ActionUnavailable"] = "That recovery action is not available yet",
        ["V2.Shell.Announce.CloseDialogFirst"] = "Close the open dialog before changing the page",
        ["V2.Shell.Announce.CaptureUnavailable"] = "Capture execution is not connected in this build",
        ["V2.Shell.Announce.CaptureRequested"] = "Requested capture intent: {0}",
        ["V2.Shell.Announce.CaptureRequestFailed"] = "The capture request could not be handed off",
        ["V2.Shell.Announce.CaptureRequestCancelled"] = "Capture request cancelled",
        ["V2.Shell.Announce.CaptureActionRequested"] = "Capture action requested",
        ["V2.Shell.Announce.SearchUnavailable"] = "Search is not connected in this host",
        ["V2.Shell.Announce.PersistenceRetrying"] = "Trying preview storage again",
        ["V2.Shell.Announce.PersistenceRestored"] = "Preview changes are saving again",
    };

    /// <remarks>
    /// [#314] The top bar, rail, sub-tab and banner words moved to Localization/Strings as "Shell.*".
    /// Registries still name them by their old key, so a key no longer here is looked up there.
    /// Setup's and Home's words followed as "Setup.*" the same way.
    /// </remarks>
    public static string Get(string key) =>
        English.TryGetValue(key ?? throw new ArgumentNullException(nameof(key)), out var text)
            ? text
            : TarkovCompanion.App.Localization.ShellText.Moved(key)
                ?? TarkovCompanion.App.Localization.SetupText.Moved(key)
                ?? throw new KeyNotFoundException($"The shell has no text for '{key}'.");

    public static string Format(string key, CultureInfo culture, params object?[] arguments) =>
        string.Format(culture, Get(key), arguments);

    /// <summary>How long ago something was observed, computed from its own time, never from now alone.</summary>
    public static string Age(DateTimeOffset observedUtc, DateTimeOffset nowUtc, CultureInfo culture)
    {
        var age = nowUtc - observedUtc;
        return age < TimeSpan.Zero ? Get("V2.Shell.Age.Future")
            : age < TimeSpan.FromMinutes(1) ? Format("V2.Shell.Age.Seconds", culture, (int)age.TotalSeconds)
            : age < TimeSpan.FromHours(1) ? Format("V2.Shell.Age.Minutes", culture, (int)age.TotalMinutes)
            : age < TimeSpan.FromDays(2) ? Format("V2.Shell.Age.Hours", culture, (int)age.TotalHours)
            : Format("V2.Shell.Age.Days", culture, (int)age.TotalDays);
    }
}
