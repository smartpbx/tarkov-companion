namespace TarkovCompanion.Application.Services.Notifications;

/// <summary>
/// What each notification looks like, for the "test this" button beside it in Setup.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Several of the six can only be seen by waiting for something to go wrong —
/// a failed refresh, a dead relay — which is a poor way to find out that a switch does nothing or
/// that the wording is unreadable on a dark second monitor. Pressing the button shows the real
/// thing through the real channel; only the words are invented, and they say so.
/// </remarks>
public static class NotificationSamples
{
    // [#314] The words (each kind's name, its sentence, the test pop-up) are the App's:
    // SetupText.NotificationTitle, NotificationDescription and NotificationSample.

    /// <summary>Whether this one is allowed to interrupt a raid. Exactly one is.</summary>
    public static bool FiresDuringRaid(NotificationKind kind) => kind == NotificationKind.SquadMark;

    /// <summary>The six, in the order Setup lists them: the one that matters most first.</summary>
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
