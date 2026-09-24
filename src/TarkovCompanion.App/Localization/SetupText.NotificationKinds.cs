using TarkovCompanion.Application.Services.Notifications;

namespace TarkovCompanion.App.Localization;

/// <summary>Each notification's name, sentence and test pop-up (#314); NotificationSamples lists the kinds.</summary>
public static partial class SetupText
{
    /// <summary>The short name of the switch.</summary>
    public static string NotificationTitle(NotificationKind kind) =>
        Defined(kind) ? UiText.Get($"Setup.NotificationKind.{kind}.Title") : string.Empty;

    /// <summary>One sentence for the row in Setup that describes it.</summary>
    public static string NotificationDescription(NotificationKind kind) =>
        Defined(kind) ? UiText.Get($"Setup.NotificationKind.{kind}.Description") : string.Empty;

    /// <summary>A real request, with invented content that says it is a test, for the test button.</summary>
    public static NotificationRequest NotificationSample(NotificationKind kind, DateTimeOffset nowUtc) => kind switch
    {
        NotificationKind.SquadMark => Sample(kind, 3, nowUtc),
        NotificationKind.DebriefReady => Sample(kind, 1, nowUtc),
        NotificationKind.DataRefreshFailed => Sample(kind, 2, nowUtc),
        NotificationKind.UpdateReady => Sample(kind, 1, nowUtc),
        NotificationKind.RelayUnreachable => Sample(kind, 1, nowUtc),
        NotificationKind.FleaSold => Sample(kind, 2, nowUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static NotificationRequest Sample(NotificationKind kind, int count, DateTimeOffset nowUtc) => new(
        kind,
        UiText.Get($"Setup.NotificationKind.{kind}.SampleTitle"),
        UiText.Get($"Setup.NotificationKind.{kind}.SampleBody"),
        count,
        nowUtc);

    private static bool Defined(NotificationKind kind) => NotificationSamples.All.Contains(kind);
}
