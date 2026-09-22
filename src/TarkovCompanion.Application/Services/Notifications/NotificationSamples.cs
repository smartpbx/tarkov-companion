using System.Globalization;

namespace TarkovCompanion.Application.Services.Notifications;

/// <summary>
/// What each notification looks like, for the "test this" button beside it in Setup.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Four of the five can only be seen by waiting for something to go wrong —
/// a failed refresh, a dead relay — which is a poor way to find out that a switch does nothing or
/// that the wording is unreadable on a dark second monitor. Pressing the button shows the real
/// thing through the real channel; only the words are invented, and they say so.
/// </remarks>
public static class NotificationSamples
{
    /// <summary>One sentence per kind, for the row in Setup that describes it.</summary>
    public static string Describe(NotificationKind kind) => kind switch
    {
        NotificationKind.SquadMark => "A squadmate drops a ping or a mark while you are in a raid.",
        NotificationKind.DebriefReady => "A raid ends and its debrief is ready to read.",
        NotificationKind.DataRefreshFailed => "The game-data refresh fails, naming what did not answer.",
        NotificationKind.UpdateReady => "A new build has downloaded and is waiting to be installed.",
        NotificationKind.RelayUnreachable => "The squad relay stops answering while sharing is on.",
        NotificationKind.FleaSold => "A flea offer sells.",
        _ => string.Empty,
    };

    /// <summary>The short name of the switch.</summary>
    public static string Title(NotificationKind kind) => kind switch
    {
        NotificationKind.SquadMark => "Squadmate marks",
        NotificationKind.DebriefReady => "Debrief ready",
        NotificationKind.DataRefreshFailed => "Data refresh failed",
        NotificationKind.UpdateReady => "Update ready",
        NotificationKind.RelayUnreachable => "Relay unreachable",
        NotificationKind.FleaSold => "Flea offer sold",
        _ => string.Empty,
    };

    /// <summary>Whether this one is allowed to interrupt a raid. Exactly one is.</summary>
    public static bool FiresDuringRaid(NotificationKind kind) => kind == NotificationKind.SquadMark;

    /// <summary>A real request, with invented content, for the test button.</summary>
    public static NotificationRequest For(NotificationKind kind, DateTimeOffset nowUtc) => kind switch
    {
        NotificationKind.SquadMark => new(
            kind,
            string.Create(CultureInfo.CurrentCulture, $"3 marks from Ferret"),
            "On the raid map. (Test)",
            3,
            nowUtc),
        NotificationKind.DebriefReady => new(
            kind,
            "Raid over",
            "The debrief is ready. (Test)",
            1,
            nowUtc),
        NotificationKind.DataRefreshFailed => new(
            kind,
            "Game data did not refresh",
            "items and tasks did not answer. The local copy still stands. (Test)",
            2,
            nowUtc),
        NotificationKind.UpdateReady => new(
            kind,
            "Update ready",
            "A newer build is downloaded and waiting in Setup › Updates. (Test)",
            1,
            nowUtc),
        NotificationKind.RelayUnreachable => new(
            kind,
            "Squad relay unreachable",
            "Sharing is on but the relay stopped answering. (Test)",
            1,
            nowUtc),
        NotificationKind.FleaSold => new(
            kind,
            "2 flea offers sold",
            "3 items. Intel › Flea lists them. (Test)",
            2,
            nowUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The five, in the order Setup lists them: the one that matters most first.</summary>
    public static IReadOnlyList<NotificationKind> All { get; } =
    [
        NotificationKind.SquadMark,
        NotificationKind.DebriefReady,
        NotificationKind.FleaSold,
        NotificationKind.RelayUnreachable,
        NotificationKind.DataRefreshFailed,
        NotificationKind.UpdateReady,
    ];
}
