using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Notifications;

/// <summary>
/// The only things this application interrupts somebody for.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Five, and the list is closed. Clayton plays on one screen with the
/// companion on another and is not looking at it during a fight, so the bar for adding a sixth is
/// "he would want to be told this while being shot at", and almost nothing clears it. Everything
/// else the application knows is already on a page he can look at when he wants to.
/// </remarks>
public enum NotificationKind
{
    /// <summary>A squadmate dropped a ping or a mark. The only one that fires during a raid.</summary>
    SquadMark = 1,

    /// <summary>A raid ended and its debrief is ready to read.</summary>
    DebriefReady,

    /// <summary>The game-data refresh failed, naming the endpoints that did not answer.</summary>
    DataRefreshFailed,

    /// <summary>A newer build has been downloaded and verified, and is waiting to be installed.</summary>
    UpdateReady,

    /// <summary>The relay stopped answering while sharing was switched on.</summary>
    RelayUnreachable,

    /// <summary>
    /// A flea offer sold. Waits for the raid to end like the others; the game posts these while
    /// the player cannot see them, and a second screen is where they get read (#314).
    /// </summary>
    FleaSold,
}

/// <summary>
/// Which notifications are on, and whether any of them may draw a window.
/// </summary>
/// <remarks>
/// All six start on, because each one is already the answer to "would he want to know". What
/// starts off is the pop-up: <see cref="ShowsDesktopPopup"/> is the only setting here that can put
/// something on screen by itself, and "nothing pops over a full-screen game unless the player
/// asked for it" is the rule it exists to keep. With it off, a notification is a tray icon that
/// changes and a count beside it, which costs a glance and never a death.
/// </remarks>
public sealed record NotificationSettings
{
    public bool SquadMark { get; init; } = true;

    public bool DebriefReady { get; init; } = true;

    public bool DataRefreshFailed { get; init; } = true;

    public bool UpdateReady { get; init; } = true;

    public bool RelayUnreachable { get; init; } = true;

    public bool FleaSold { get; init; } = true;

    /// <summary>Off until asked for. See the remarks on this record.</summary>
    public bool ShowsDesktopPopup { get; init; }

    /// <summary>
    /// Whether the pop-up keeps quiet between <see cref="QuietFromHour"/> and
    /// <see cref="QuietToHour"/>, local time. The tray count still counts: quiet hours stop
    /// something being drawn, never something being recorded.
    /// </summary>
    public bool QuietHours { get; init; }

    /// <summary>The local hour (0-23) quiet hours start.</summary>
    public int QuietFromHour { get; init; } = 23;

    /// <summary>The local hour (0-23) quiet hours end. Earlier than the start means past midnight.</summary>
    public int QuietToHour { get; init; } = 8;

    public static NotificationSettings Default { get; } = new();

    public bool IsEnabled(NotificationKind kind) => kind switch
    {
        NotificationKind.SquadMark => SquadMark,
        NotificationKind.DebriefReady => DebriefReady,
        NotificationKind.DataRefreshFailed => DataRefreshFailed,
        NotificationKind.UpdateReady => UpdateReady,
        NotificationKind.RelayUnreachable => RelayUnreachable,
        NotificationKind.FleaSold => FleaSold,
        _ => false,
    };

    /// <summary>
    /// Whether a local time of day falls in quiet hours. From 23 to 8 covers 23:00 to 07:59; a
    /// start equal to the end is an empty window rather than the whole day, so a slip of the
    /// picker can never silence everything.
    /// </summary>
    public bool IsQuietAt(TimeOnly localTime)
    {
        if (!QuietHours)
        {
            return false;
        }

        var from = Math.Clamp(QuietFromHour, 0, 23);
        var to = Math.Clamp(QuietToHour, 0, 23);
        var hour = localTime.Hour;
        return from <= to
            ? hour >= from && hour < to
            : hour >= from || hour < to;
    }

    public NotificationSettings With(NotificationKind kind, bool enabled) => kind switch
    {
        NotificationKind.SquadMark => this with { SquadMark = enabled },
        NotificationKind.DebriefReady => this with { DebriefReady = enabled },
        NotificationKind.DataRefreshFailed => this with { DataRefreshFailed = enabled },
        NotificationKind.UpdateReady => this with { UpdateReady = enabled },
        NotificationKind.RelayUnreachable => this with { RelayUnreachable = enabled },
        NotificationKind.FleaSold => this with { FleaSold = enabled },
        _ => this,
    };
}

/// <summary>One thing worth telling somebody, already written out.</summary>
/// <param name="Kind">Which of the six this is.</param>
/// <param name="Title">Three or four words, which is all a tray balloon shows.</param>
/// <param name="Body">One sentence underneath it.</param>
/// <param name="Count">How many events this stands for; 1 unless a burst was coalesced.</param>
/// <param name="RaisedUtc">When it was decided, not when it is drawn.</param>
public sealed record NotificationRequest(
    NotificationKind Kind,
    string Title,
    string Body,
    int Count,
    DateTimeOffset RaisedUtc);

/// <summary>Text safe to let Windows repeat on a locked screen.</summary>
/// <remarks>
/// This deliberately cannot carry the original notification request. A platform channel accepts
/// only this projection, so a later flea alert cannot accidentally put an item name or value on
/// the lock screen merely because its in-app wording became more useful.
/// </remarks>
public sealed record LockScreenNotification(string Title, string Body);

/// <summary>A native operating-system notification surface, where content may outlive the window.</summary>
public interface INativeNotificationChannel
{
    bool IsAvailable { get; }

    void Show(LockScreenNotification notification);
}

public static class NotificationPrivacy
{
    /// <summary>Removes names, values, endpoints and other detail before Windows can retain it.</summary>
    public static LockScreenNotification ForLockScreen(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            NotificationKind.SquadMark => new("Squad update", "Open Tarkov Companion to view it."),
            NotificationKind.DebriefReady => new("Raid over", "The debrief is ready."),
            NotificationKind.DataRefreshFailed => new("Game data needs attention", "Open Tarkov Companion for details."),
            NotificationKind.UpdateReady => new("Update ready", "Open Tarkov Companion for details."),
            NotificationKind.RelayUnreachable => new("Squad relay unreachable", "Open Tarkov Companion for details."),
            NotificationKind.FleaSold => new("Flea offer sold", "Open Tarkov Companion for details."),
            _ => new("Tarkov Companion", "Open Tarkov Companion for details."),
        };
    }
}

/// <summary>One squadmate mark or ping, as the coordinator needs to see it.</summary>
/// <param name="Id">The relay's own id, which is what stops one mark being announced twice.</param>
/// <param name="By">Whose it is. Ours are never announced back to us.</param>
/// <param name="IsPing">A ping ("look here now") rather than a waypoint, for the wording.</param>
public readonly record struct SquadMarkInput(long Id, string By, bool IsPing);

/// <summary>One flea sale, as the coordinator needs to see it.</summary>
/// <param name="OfferId">The game's offer id, which is what stops one sale being announced twice.</param>
/// <param name="Count">How many items went with it.</param>
/// <param name="WrittenUtc">When the game wrote it; null when its timestamp could not be read.</param>
public readonly record struct FleaSaleInput(string OfferId, int Count, DateTimeOffset? WrittenUtc);

/// <summary>
/// Everything the coordinator looks at, in one record it can be handed repeatedly.
/// </summary>
/// <remarks>
/// A snapshot rather than a stream of events, because that is the shape the runtime state store
/// already publishes; the coordinator does the diffing. It means the rules can be tested by
/// handing them two records and reading what came back, with no timers and no subscriptions.
/// </remarks>
public sealed record NotificationInputs
{
    public required DateTimeOffset NowUtc { get; init; }

    public RaidLifecycleState RaidState { get; init; } = RaidLifecycleState.Unknown;

    public Guid? RaidId { get; init; }

    /// <summary>This player's own name on the relay, so their own marks stay silent.</summary>
    public string? PlayerName { get; init; }

    public bool IsSharing { get; init; }

    /// <summary>When the relay stopped answering, or null while it is answering.</summary>
    public DateTimeOffset? RelayStaleSince { get; init; }

    public IReadOnlyList<SquadMarkInput> SquadMarks { get; init; } = [];

    /// <summary>The endpoints that did not refresh, by name. Empty when the refresh was clean.</summary>
    public IReadOnlyList<string> FailedDataEndpoints { get; init; } = [];

    /// <summary>The build waiting to be installed, or null when there is nothing to install.</summary>
    public string? UpdateReadyBuild { get; init; }

    /// <summary>The flea sales seen this session.</summary>
    public IReadOnlyList<FleaSaleInput> FleaSales { get; init; } = [];
}

/// <summary>Where the notification settings are kept between runs.</summary>
public interface INotificationSettingsStore
{
    Task<NotificationSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken);
}

/// <summary>Whatever actually puts a notification in front of somebody.</summary>
/// <remarks>
/// One method, so the tray, an in-window toast and a test double are the same thing to the rules
/// above. Nothing here decides whether to show; by the time a request arrives that is settled.
/// </remarks>
public interface INotificationChannel
{
    void Show(NotificationRequest request);
}
