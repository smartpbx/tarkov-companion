using TarkovCompanion.App.Localization;

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
    // [#314] The words live in the string table. Each detail paragraph is stored there as
    // sentence-sized pieces joined with a space, because sweep-prose.sh holds every table entry to
    // 120 characters. A getter rather than a stored list, so a language chosen after this class
    // is first touched still reaches it.
    public static IReadOnlyList<SetupDisclosure> About =>
    [
        new(
            SetupAnchors.WhatItIs,
            SetupText.AboutWhatItIsTitle,
            SetupText.AboutWhatItIsSummary,
            SetupText.AboutWhatItIsDetail),
        new(
            SetupAnchors.AntiCheat,
            SetupText.AboutAntiCheatTitle,
            SetupText.AboutAntiCheatSummary,
            SetupText.AboutAntiCheatDetail),
        new(
            SetupAnchors.Predictions,
            SetupText.AboutPredictionsTitle,
            SetupText.AboutPredictionsSummary,
            SetupText.AboutPredictionsDetail),
        new(
            SetupAnchors.Notices,
            SetupText.AboutNoticesTitle,
            SetupText.AboutNoticesSummary,
            SetupText.AboutNoticesDetail),
    ];

    public static IReadOnlyList<SetupDisclosure> DataPrivacy =>
    [
        new(
            SetupAnchors.Stored,
            SetupText.InfoStoredTitle,
            SetupText.InfoStoredSummary,
            SetupText.InfoStoredDetail),
        new(
            SetupAnchors.LeavesComputer,
            SetupText.InfoLeavesComputerTitle,
            SetupText.InfoLeavesComputerSummary,
            SetupText.InfoLeavesComputerDetail),
        new(
            SetupAnchors.CaptureRetention,
            SetupText.InfoCaptureRetentionTitle,
            SetupText.InfoCaptureRetentionSummary,
            SetupText.InfoCaptureRetentionDetail),
        new(
            SetupAnchors.SharingScope,
            SetupText.InfoSharingScopeTitle,
            SetupText.InfoSharingScopeSummary,
            SetupText.InfoSharingScopeDetail),
        new(
            SetupAnchors.Reports,
            SetupText.InfoReportsTitle,
            SetupText.InfoReportsSummary,
            SetupText.InfoReportsDetail),
        new(
            SetupAnchors.Methodology,
            SetupText.InfoMethodologyTitle,
            SetupText.InfoMethodologySummary,
            SetupText.InfoMethodologyDetail),
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
