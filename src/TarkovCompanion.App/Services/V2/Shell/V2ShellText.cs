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
        ["V2.Shell.AppName"] = "Tarkov Companion",
        ["V2.Shell.Provisional"] = "provisional - #265 not yet run",
        ["V2.Shell.WindowTitle"] = "Tarkov Companion · {0} · {1}",
        ["V2.Shell.Variant.A"] = "Variant A · workspace rail",
        ["V2.Shell.Variant.B"] = "Variant B · workflow hub",
        ["V2.Shell.Region.Header"] = "Companion header",
        ["V2.Shell.Region.Navigation"] = "Destinations",
        ["V2.Shell.Region.Sections"] = "Sections",
        ["V2.Shell.Region.SetupSection"] = "Setup",
        ["V2.Shell.Region.Status"] = "Status announcements",
        ["V2.Shell.Region.Main"] = "Current workspace",
        ["V2.Shell.Nav.Current"] = "Current page",
        ["V2.Shell.Nav.NotCurrent"] = "Available page",

        ["V2.Shell.Route.Home"] = "Home",
        ["V2.Shell.Route.Raid"] = "Raid",
        ["V2.Shell.Route.Loot"] = "Loot decision",
        ["V2.Shell.Route.Items"] = "Items",
        ["V2.Shell.Route.Ammo"] = "Ammo",
        ["V2.Shell.Route.Keys"] = "Keys",
        ["V2.Shell.Route.Flea"] = "Flea",
        ["V2.Shell.Route.Item"] = "Item details",
        ["V2.Shell.Route.Stash"] = "Stash scan",
        ["V2.Shell.Route.Quests"] = "Quests",
        ["V2.Shell.Route.Hideout"] = "Hideout",
        ["V2.Shell.Route.Loadout"] = "Loadout",
        ["V2.Shell.Route.Events"] = "Events",
        ["V2.Shell.Route.Squad"] = "Squad",
        ["V2.Shell.Route.Group"] = "Group",
        ["V2.Shell.Route.Tablet"] = "Tablet preview",
        ["V2.Shell.Route.History"] = "History",
        ["V2.Shell.Route.Settings"] = "Settings",

        ["V2.Shell.Label.Raid"] = "Raid",
        ["V2.Shell.Label.Intel"] = "Intel",
        ["V2.Shell.Label.Plan"] = "Plan",
        ["V2.Shell.Label.Team"] = "Team",
        ["V2.Shell.Label.Debrief"] = "Debrief",
        ["V2.Shell.Label.SetupAdmin"] = "Setup & Admin",
        ["V2.Shell.Label.Home"] = "Home",
        ["V2.Shell.Label.Prepare"] = "Prepare",
        ["V2.Shell.Label.History"] = "History",
        ["V2.Shell.Label.Setup"] = "Setup",

        ["V2.Shell.Search.Label"] = "Search",
        ["V2.Shell.Search.Heading"] = "Search",
        ["V2.Shell.Search.Placeholder"] = "Items, ammo, keys",
        ["V2.Shell.Search.Open"] = "Search",
        ["V2.Shell.Address.Label"] = "Address",
        ["V2.Shell.Address.Open"] = "Open",
        ["V2.Shell.BackTo"] = "Back to {0}",
        ["V2.Shell.Close"] = "Close",

        ["V2.Shell.Command.Back"] = "Back",
        ["V2.Shell.Command.Forward"] = "Forward",
        ["V2.Shell.Command.Capture"] = "Capture",
        ["V2.Shell.Command.Health"] = "Setup status",
        ["V2.Shell.Command.Palette"] = "Commands",
        ["V2.Shell.Command.Search"] = "Search",
        ["V2.Shell.Command.CopyAddress"] = "Copy address",
        ["V2.Shell.Command.Pin"] = "Pin or unpin this page",
        ["V2.Shell.Command.Close"] = "Close the open panel",
        ["V2.Shell.Command.NextRegion"] = "Next region",
        ["V2.Shell.Command.PreviousRegion"] = "Previous region",
        ["V2.Shell.Command.CaptureShortcut"] = "Turn the Capture shortcut on or off",
        ["V2.Shell.Command.ResetPreview"] = "Reset this preview's navigation",

        ["V2.Shell.Palette.Heading"] = "Commands",
        ["V2.Shell.Palette.Query"] = "Type a command or paste an address",
        ["V2.Shell.Palette.Recents"] = "Recent",
        ["V2.Shell.Palette.Pins"] = "Pinned",
        ["V2.Shell.Palette.OpenAddress"] = "Open {0}",
        ["V2.Shell.Palette.Empty"] = "No command matches",

        ["V2.Shell.Health.Heading"] = "Setup status",
        ["V2.Shell.Health.NeedsActionOne"] = "1 setup item needs action",
        ["V2.Shell.Health.NeedsActionMany"] = "{0} setup items need action",
        ["V2.Shell.Health.Unconfirmed"] = "Setup: {0} unconfirmed",
        ["V2.Shell.Health.Clear"] = "Setup: nothing needs action",
        ["V2.Shell.Health.OpenSetup"] = "Open {0}",

        ["V2.Shell.Readiness.Heading"] = "Get ready",
        ["V2.Shell.Readiness.Summary"] = "{0} of {1} checks ready · {2} need action · {3} unconfirmed",
        ["V2.Shell.Readiness.Open"] = "Open",
        ["V2.Shell.Continue.Heading"] = "Continue",
        ["V2.Shell.Continue.Empty"] = "Nothing to continue yet",

        ["V2.Shell.Check.GameLog"] = "Game log folder",
        ["V2.Shell.Check.Screenshots"] = "Screenshot folder",
        ["V2.Shell.Check.Recognition"] = "Text recognition",
        ["V2.Shell.Check.GameData"] = "Game data",
        ["V2.Shell.Check.Profile"] = "Profile",
        ["V2.Shell.Check.GroupSharing"] = "Group sharing",

        ["V2.Shell.Status.Ready"] = "Ready",
        ["V2.Shell.Status.NeedsAction"] = "Needs action",
        ["V2.Shell.Status.Optional"] = "Optional",
        ["V2.Shell.Status.Unconfirmed"] = "Unconfirmed",
        ["V2.Shell.Status.Failed"] = "Failed",
        ["V2.Shell.Status.Checking"] = "Checking",

        ["V2.Shell.Detail.DemoFixture"] = "Demo fixture: no live game access",
        ["V2.Shell.Detail.Profile"] = "{0} · {1}",
        ["V2.Shell.Detail.ProfileNotLoaded"] = "Profile not loaded yet",
        ["V2.Shell.Detail.Sharing"] = "Sharing with {0} group members",
        ["V2.Shell.Detail.NotSharing"] = "Off: nothing is shared",
        ["V2.Shell.Detail.GameData"] = "{0:N0} items · synced {1}",
        ["V2.Shell.Detail.NoStashScan"] = "No stash scan yet",
        ["V2.Shell.Detail.NoTablet"] = "No paired tablet. Pairing is not in this build.",
        ["V2.Shell.Detail.OfflineCached"] = "Offline. Using {0:N0} cached items synced {1}",
        ["V2.Shell.Detail.UndatedData"] = "{0:N0} items with no sync time",
        ["V2.Shell.Detail.StaleData"] = "Game data synced {0}",
        ["V2.Shell.Detail.NeverSynced"] = "never",

        ["V2.Shell.Age.Seconds"] = "{0}s ago",
        ["V2.Shell.Age.Minutes"] = "{0}m ago",
        ["V2.Shell.Age.Hours"] = "{0}h ago",
        ["V2.Shell.Age.Days"] = "{0}d ago",
        ["V2.Shell.Age.Future"] = "at a future time",

        ["V2.Shell.State.Ready"] = "Ready",
        ["V2.Shell.State.Loading"] = "Loading",
        ["V2.Shell.State.Empty"] = "Nothing here yet",
        ["V2.Shell.State.Offline"] = "Offline",
        ["V2.Shell.State.Stale"] = "Stale",
        ["V2.Shell.State.Partial"] = "Partial",
        ["V2.Shell.State.Denied"] = "Permission denied",
        ["V2.Shell.State.Failed"] = "Failed",
        ["V2.Shell.StateGlyph.Ready"] = "✓",
        ["V2.Shell.StateGlyph.Loading"] = "…",
        ["V2.Shell.StateGlyph.Empty"] = "○",
        ["V2.Shell.StateGlyph.Offline"] = "⊘",
        ["V2.Shell.StateGlyph.Stale"] = "◷",
        ["V2.Shell.StateGlyph.Partial"] = "◔",
        ["V2.Shell.StateGlyph.Denied"] = "⌕",
        ["V2.Shell.StateGlyph.Failed"] = "×",
        ["V2.Shell.State.AutomationName"] = "{0}. {1}. {2}",

        ["V2.Shell.Remainder.All"] = "Everything is available",
        ["V2.Shell.Remainder.Local"] = "Local pages, history and settings still work",
        ["V2.Shell.Remainder.Cached"] = "Everything still works, labelled with its age",
        ["V2.Shell.Remainder.Stash"] = "Capture and every other page still work",
        ["V2.Shell.Remainder.Tablet"] = "Team, marks and group sharing still work",

        ["V2.Shell.Action.OpenCapture"] = "Open Capture",
        ["V2.Shell.Action.OpenTeam"] = "Open Team",
        ["V2.Shell.Action.SyncNow"] = "Sync now",
        ["V2.Shell.Action.OpenSetup"] = "Open Setup",
        ["V2.Shell.Action.OpenReadiness"] = "Open",

        ["V2.Shell.Capture.Heading"] = "Capture",
        ["V2.Shell.Capture.NotArmed"] = "Not armed: screenshots use Auto-detect",
        ["V2.Shell.Capture.Guidance"] = "Press the game's own screenshot key. The companion reads the saved file.",
        ["V2.Shell.Capture.Watching"] = "Screenshot folder is watched",
        ["V2.Shell.Capture.NotWatching"] = "Screenshot folder is not watched",
        ["V2.Shell.Capture.IntentHeading"] = "Intent",
        ["V2.Shell.Capture.IntentUnavailable"] = "Choosing an intent is not in this build",
        ["V2.Shell.Capture.PriorHeading"] = "Last capture",
        ["V2.Shell.Capture.NoPrior"] = "No capture yet",
        ["V2.Shell.Capture.ShortcutOn"] = "Window shortcut on",
        ["V2.Shell.Capture.ShortcutOff"] = "Window shortcut off",
        ["V2.Shell.Capture.Review"] = "Review",
        ["V2.Shell.Capture.Details"] = "Details",
        ["V2.Shell.Capture.Reference"] = "Reference {0}",
        ["V2.Shell.Capture.Evidence"] = "{0} · {1}",

        ["V2.Shell.Intent.Auto"] = "Auto-detect",
        ["V2.Shell.Intent.Loot"] = "Loot decision",
        ["V2.Shell.Intent.Stash"] = "Full stash",
        ["V2.Shell.Intent.Ammo"] = "Ammo",
        ["V2.Shell.Intent.Keys"] = "Keys",
        ["V2.Shell.Intent.QuestItems"] = "Quest items",
        ["V2.Shell.Intent.ExtractsAndMap"] = "Map and extracts",
        ["V2.Shell.Intent.HealthAndCharacter"] = "Health and character",
        ["V2.Shell.Intent.Flea"] = "Flea listings you opened",

        ["V2.Shell.Intel.Heading"] = "Intel",
        ["V2.Shell.Intel.Item"] = "Item address: {0}",
        ["V2.Shell.Intel.Close"] = "Close Intel",
        ["V2.Shell.Intel.Loading"] = "Reading item facts from the local catalog",
        ["V2.Shell.Intel.NotFound"] = "No item in the local catalog has this address",
        ["V2.Shell.Intel.ReadFailed"] = "Item facts could not be read: {0}",
        ["V2.Shell.Intel.ShortName"] = "Short name",
        ["V2.Shell.Intel.Category"] = "Category",
        ["V2.Shell.Intel.Size"] = "Size",
        ["V2.Shell.Intel.SizeValue"] = "{0} × {1} · {2} slots",
        ["V2.Shell.Intel.Price"] = "Best price",
        ["V2.Shell.Intel.PriceValue"] = "{0:N0} ₽ · {1}",
        ["V2.Shell.Intel.PriceUnknown"] = "Unknown: no price in the local catalog",
        ["V2.Shell.Intel.PriceEvidence"] = "{0} · {1}",
        ["V2.Shell.Intel.Flea"] = "Flea market",
        ["V2.Shell.Intel.FleaAllowed"] = "Allowed",
        ["V2.Shell.Intel.FleaNotAllowed"] = "Not allowed",
        ["V2.Shell.Intel.Catalog"] = "Catalog",

        ["V2.Shell.Banner.Sharing"] = "Sharing with your group",
        ["V2.Shell.Banner.OpenGroup"] = "Open Group",

        ["V2.Shell.Context.Profile"] = "Profile: {0}",
        ["V2.Shell.Context.NoProfile"] = "Profile: not loaded",
        ["V2.Shell.Context.RaidOnMap"] = "{0} · {1}",

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
    };

    public static string Get(string key) =>
        English.TryGetValue(key ?? throw new ArgumentNullException(nameof(key)), out var text)
            ? text
            : throw new KeyNotFoundException($"The shell has no text for '{key}'.");

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
