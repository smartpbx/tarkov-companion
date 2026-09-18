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
}

/// <summary>
/// Which notifications are on, and whether any of them may draw a window.
/// </summary>
/// <remarks>
/// All five start on, because each one is already the answer to "would he want to know". What
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

    /// <summary>Off until asked for. See the remarks on this record.</summary>
    public bool ShowsDesktopPopup { get; init; }

    public static NotificationSettings Default { get; } = new();

    public bool IsEnabled(NotificationKind kind) => kind switch
    {
        NotificationKind.SquadMark => SquadMark,
        NotificationKind.DebriefReady => DebriefReady,
        NotificationKind.DataRefreshFailed => DataRefreshFailed,
        NotificationKind.UpdateReady => UpdateReady,
        NotificationKind.RelayUnreachable => RelayUnreachable,
        _ => false,
    };

    public NotificationSettings With(NotificationKind kind, bool enabled) => kind switch
    {
        NotificationKind.SquadMark => this with { SquadMark = enabled },
        NotificationKind.DebriefReady => this with { DebriefReady = enabled },
        NotificationKind.DataRefreshFailed => this with { DataRefreshFailed = enabled },
        NotificationKind.UpdateReady => this with { UpdateReady = enabled },
        NotificationKind.RelayUnreachable => this with { RelayUnreachable = enabled },
        _ => this,
    };
}

/// <summary>One thing worth telling somebody, already written out.</summary>
/// <param name="Kind">Which of the five this is.</param>
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

/// <summary>One squadmate mark or ping, as the coordinator needs to see it.</summary>
/// <param name="Id">The relay's own id, which is what stops one mark being announced twice.</param>
/// <param name="By">Whose it is. Ours are never announced back to us.</param>
/// <param name="IsPing">A ping ("look here now") rather than a waypoint, for the wording.</param>
public readonly record struct SquadMarkInput(long Id, string By, bool IsPing);

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
