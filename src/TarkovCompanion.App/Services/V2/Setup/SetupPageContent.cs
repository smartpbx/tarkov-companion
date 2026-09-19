namespace TarkovCompanion.App.Services.V2.Setup;

/// <summary>One layered item on the About or Data &amp; Privacy page: a headline, then detail on demand.</summary>
public sealed record SetupDisclosure(string Id, string Title, string Summary, string Detail);

/// <summary>
/// The words of Setup › About and Setup › Data &amp; Privacy, taken from <c>docs/SAFETY.md</c> and
/// <c>docs/PRIVACY.md</c> and kept true to them.
/// </summary>
/// <remarks>
/// #292 asked for canonical, linkable pages for safety, anti-cheat, data, privacy, capture retention and
/// sharing scope, with "layered summaries and expandable detail instead of a catch-all prose wall". So each
/// item is a one-line summary the page always shows and a short paragraph behind it. Nothing here is
/// repeated on other pages: everywhere else says at most "details" and links here.
/// The ids are the deep-link anchors; <see cref="SetupAnchors"/> names them for callers.
/// </remarks>
public static class SetupPageContent
{
    public static IReadOnlyList<SetupDisclosure> About { get; } =
    [
        new(
            SetupAnchors.WhatItIs,
            "What this is",
            "A second-screen companion for Escape from Tarkov.",
            "It runs in its own window, on a second monitor or a tablet. It reads the screenshots and logs the game writes itself, "
            + "and public game data, and shows maps, quests, hideout needs and loot decisions. It never draws over the game."),
        new(
            SetupAnchors.AntiCheat,
            "What it never does",
            "It only reads what the game already wrote.",
            "It never reads or writes the game's memory, injects code or hooks the renderer, or looks at the game's network traffic. "
            + "It never sends keyboard, mouse or controller input, and never automates flea, inventory or combat actions. "
            + "It has no overlay, no radar and no live enemy tracking."),
        new(
            SetupAnchors.Predictions,
            "Estimates and history",
            "Anything predicted says so. Nothing is a live position.",
            "Traffic and spawn views are modelled from static spawns, map topology, elapsed raid time and past evidence, and are labelled "
            + "Historical, Modelled or Predicted. Where freshness or confidence could change a decision, it is shown beside the figure."),
        new(
            SetupAnchors.Notices,
            "Open-source notices",
            "Built on open-source components.",
            "Every component and its licence is listed in THIRD_PARTY_NOTICES.md in the source repository, "
            + "which is public at github.com/smartpbx/tarkov-companion."),
    ];

    public static IReadOnlyList<SetupDisclosure> DataPrivacy { get; } =
    [
        new(
            SetupAnchors.Stored,
            "What is kept on this computer",
            "Your progress, the game data it downloaded, your raid history and settings.",
            "Profiles and their quest, hideout and wishlist progress; the cached public game data; raid history; and your settings all "
            + "live in the data folder. Nothing in it is uploaded unless you turn sharing on or send a report."),
        new(
            SetupAnchors.LeavesComputer,
            "What leaves this computer",
            "Game data requests, update checks, and only what you choose to share.",
            "Game data is fetched from json.tarkov.dev and the request names only the game mode and language. The update check asks the update "
            + "feed for a newer build. Sharing, when it is on, sends your position, marks and waypoints to the people in your room. "
            + "A problem report is sent only when you press Report a problem. There is no telemetry and no screenshot upload."),
        new(
            SetupAnchors.CaptureRetention,
            "Screenshots",
            "The app keeps no copy of a screenshot it reads.",
            "It reads the game's own screenshot files and keeps what it recognised, not the picture. Tidying old screenshots is separate: "
            + "it moves the game's own files to the recycle bin, and it is off until you turn it on."),
        new(
            SetupAnchors.SharingScope,
            "Sharing",
            "Off until you join a room.",
            "Joining a room shares your name, your map position taken from your screenshots' names, and your marks and waypoints with the "
            + "people in that room and the tablets you paired, through the relay. If you turn it on, it also shares the quests you are "
            + "working on. It does not share your screenshots or files, and leaving the room stops it."),
        new(
            SetupAnchors.Reports,
            "Problem reports",
            "A short summary, with no names, paths or pictures.",
            "The report holds counts, build numbers and yes/no facts about your setup. It has no player names, file paths, screenshots, "
            + "log lines or credentials, and it is sent only when you press Report a problem."),
        new(
            SetupAnchors.Methodology,
            "Where the game data comes from",
            "Public data from tarkov.dev, cached here.",
            "Items, maps, quests, hideout, traders and crafts come from the public json.tarkov.dev feed for the profile's game mode. "
            + "The Data section shows when it was last fetched and how much of it there is."),
    ];
}

/// <summary>The anchors a caller can open a Setup page at.</summary>
public static class SetupAnchors
{
    public const string WhatItIs = "what-it-is";
    public const string AntiCheat = "anti-cheat";
    public const string Predictions = "predictions";
    public const string Notices = "notices";
    public const string Stored = "stored-locally";
    public const string LeavesComputer = "leaves-computer";
    public const string CaptureRetention = "capture-retention";
    public const string SharingScope = "sharing-scope";
    public const string Reports = "reports";
    public const string Methodology = "data-methodology";
}
