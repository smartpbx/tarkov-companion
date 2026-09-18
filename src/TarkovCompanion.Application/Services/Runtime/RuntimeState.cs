using System.Collections.Immutable;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

public enum DataAvailability
{
    Unavailable,
    Cached,
    Current,
    Refreshing,
    DemoFixture,
    Error,
}

public sealed record CachedDataSnapshot(
    int ItemCount,
    int SyncedEndpointCount,
    DateTimeOffset? LastSuccessUtc,
    string? LastError);

public sealed record RuntimeDataState(
    DataAvailability Availability,
    int ItemCount,
    int SyncedEndpointCount,
    DateTimeOffset? UpdatedUtc,
    string Detail)
{
    /// <summary>
    /// The endpoints that did not refresh, by name. Empty after a clean refresh.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43] <see cref="Detail"/> has always named them, inside a sentence
    /// assembled for a status line. A notification has to name them too, and parsing that sentence
    /// back apart to find out which ones failed would be a second source of truth that drifts the
    /// first time the wording changes.
    /// </remarks>
    public IReadOnlyList<string> FailedEndpoints { get; init; } = [];
}

public sealed record ScanExecutionResult(
    bool IsAvailable,
    bool Succeeded,
    string? CanonicalItemId,
    string? ItemName,
    long? ValueRoubles,
    long? ValuePerSlotRoubles,
    string? Recommendation,
    Confidence Confidence,
    DateTimeOffset ObservedUtc,
    string Source,
    string Detail)
{
    /// <summary>
    /// Turns a finished scan into something the interface can show, whatever asked for it.
    /// </summary>
    /// <remarks>
    /// This projection used to live inside the adapter behind the scan button, which is why a
    /// scan driven by the game's own screenshot key reached the recogniser, produced a perfectly
    /// good result, and was then logged and dropped. The interface only ever learned about
    /// scans that came through one particular door.
    /// </remarks>
    public static ScanExecutionResult FromOutcome(ScanOutcome outcome, string source)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var selected = outcome.Recognition.Selected;
        var recommendation = outcome.Recommendation;
        var detail = outcome.Status switch
        {
            ScanCompletionStatus.Unavailable =>
                $"Scan unavailable ({outcome.DiagnosticCode ?? "no diagnostic"}); no pixels were persisted.",
            _ when selected is not null && recommendation is null && outcome.EconomicValue is { } worth =>
                $"Resolved {selected.DisplayName}, worth {worth:N0} roubles. No recommendation, because that needs raid context the scan did not have.",
            _ when selected is not null && recommendation is null =>
                $"Resolved {selected.DisplayName}; recommendation withheld because required item-context evidence was unavailable. No pixels were persisted.",
            _ when selected is not null =>
                $"Resolved {selected.DisplayName} from an in-memory scan; no pixels were persisted.",
            _ =>
                $"{outcome.Context} scan finished with {outcome.Status}; no item was auto-selected and no pixels were persisted.",
        };

        return new(
            outcome.Status != ScanCompletionStatus.Unavailable,
            selected is not null,
            selected?.CanonicalId,
            selected?.DisplayName,
            // The value comes from the item, and only falls back to the recommendation. These
            // used to be the same field, so withholding advice hid a price the application had
            // already fetched: "what is this worth" and "should you take it" are different
            // questions and only the second needs context the scanner may not have.
            outcome.EconomicValue ?? recommendation?.SelectedEconomicValue,
            outcome.ValuePerSlot ?? recommendation?.ValuePerSlot,
            recommendation?.Action.ToString(),
            selected?.Confidence ?? Confidence.Unknown,
            outcome.ObservedUtc.ToUniversalTime(),
            source,
            detail);
    }

    /// <summary>
    /// Whether this is worth putting in front of somebody, as opposed to merely having happened.
    /// </summary>
    /// <remarks>
    /// The screenshot key fires on everything a player photographs, and most of that is the
    /// game world: a wall, a corridor, a body. Publishing those would replace a good reading of
    /// an item with "Unknown scan finished with Partial" seconds later, which is worse than not
    /// reporting them at all, because the useful answer is the one that disappears.
    ///
    /// So an unprompted scan has to have found something. One somebody asked for is always
    /// worth an answer, including a disappointing one, because they are waiting for it.
    /// </remarks>
    public bool IsWorthReporting => Succeeded || !IsAvailable;

    public static ScanExecutionResult Unavailable(string detail, DateTimeOffset observedUtc) => new(
        false,
        false,
        null,
        null,
        null,
        null,
        null,
        Confidence.Unknown,
        observedUtc.ToUniversalTime(),
        "unavailable",
        detail);

    /// <summary>
    /// The scanner can run and has not been asked to yet.
    /// </summary>
    /// <remarks>
    /// Distinct from unavailable, which used to cover both and so told a player whose scanner
    /// worked perfectly the same thing it told one whose scanner could not start. "Nothing has
    /// been scanned yet" is a description of the player's evening, not of the software.
    /// </remarks>
    public static ScanExecutionResult Ready(string detail, DateTimeOffset observedUtc) => new(
        true,
        false,
        null,
        null,
        null,
        null,
        null,
        Confidence.Unknown,
        observedUtc.ToUniversalTime(),
        "ready",
        detail);
}

/// <summary>
/// What the companion is currently able to observe about Escape from Tarkov.
/// </summary>
/// <remarks>
/// Observation is ordinary file watching over the log and screenshot folders the game
/// already writes to. The companion never reads game memory or inspects traffic, so when
/// these folders cannot be found there is genuinely nothing to report, and the header must
/// say so rather than implying the game is simply idle.
/// </remarks>
public sealed record EftObservationState(
    bool IsSupported,
    bool IsWatchingLogs,
    bool IsWatchingScreenshots,
    string? LogRoot,
    string? ScreenshotRoot,
    Confidence Confidence,
    string Detail)
{
    public bool IsObserving => IsWatchingLogs || IsWatchingScreenshots;

    public static EftObservationState Unsupported { get; } = new(
        false,
        false,
        false,
        null,
        null,
        Confidence.Unknown,
        "Observing Escape from Tarkov requires Windows.");

    public static EftObservationState Idle { get; } = new(
        true,
        false,
        false,
        null,
        null,
        Confidence.Unknown,
        "Looking for the Escape from Tarkov log and screenshot folders.");
}

public sealed record ApplicationRuntimeSnapshot(
    bool IsDemoMode,
    bool IsOffline,
    bool DatabaseReady,
    RuntimeDataState Data,
    PlayerProfile? Profile,
    RaidSnapshot Raid,
    ScanExecutionResult Scan)
{
    /// <summary>A process-local publication revision, unrelated to cross-device state revision.</summary>
    public long LocalRevision { get; init; }

    public FeatureLifecycleSnapshot Lifecycle { get; init; } = FeatureLifecycleSnapshot.Empty;

    public BackgroundWorkSupervisorSnapshot Supervisor { get; init; } = BackgroundWorkSupervisorSnapshot.Empty;

    public OutboxSnapshot Outbox { get; init; } = OutboxSnapshot.Empty;

    public RuntimeResourceSnapshot Resources { get; init; } = RuntimeResourceSnapshot.Empty;

    public EftObservationState Observation { get; init; } =
        OperatingSystem.IsWindows() ? EftObservationState.Idle : EftObservationState.Unsupported;

    /// <summary>The player's party, as the game's own group notifications describe it.</summary>
    public SquadSnapshot Squad { get; init; } = SquadSnapshot.Empty;

    /// <summary>
    /// The names of the last few screenshots seen, newest first.
    /// </summary>
    /// <remarks>
    /// Kept for one reason: a name whose shape this build does not recognise yields no
    /// position, and that is invisible from every other angle. The game confirms the
    /// screenshot, the folder is right, the file is there, and the player simply never appears
    /// on anybody's map. Two players hit exactly that in one evening.
    ///
    /// Names only, never paths. Support diagnostics inspect at most three bounded names and emit
    /// only a fixed compatibility category and counts; no name, digit, or coordinate is rendered
    /// or shared.
    /// </remarks>
    public IReadOnlyList<string> RecentScreenshotNames { get; init; } = [];

    /// <summary>Flea offers the game reported as sold since the companion started.</summary>
    public FleaSalesSnapshot FleaSales { get; init; } = FleaSalesSnapshot.Empty;

    /// <summary>
    /// The group the player is sharing with, when they have chosen to share at all.
    /// </summary>
    /// <remarks>
    /// Starts switched off, and stays off until somebody turns it on. This is the only part of
    /// the application that sends anything anywhere, so its default is the one default worth
    /// being deliberate about.
    /// </remarks>
    public GroupSnapshot Group { get; init; } = GroupSnapshot.Off;
}

public interface IRuntimeStateStore
{
    event EventHandler? Changed;

    ApplicationRuntimeSnapshot Current { get; }

    void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update);
}

public sealed record RuntimeResourceSnapshot(long StateSubscriberFaults)
{
    public static RuntimeResourceSnapshot Empty { get; } = new(0);
}

/// <summary>The single published runtime snapshot, with isolated and serialized subscribers.</summary>
/// <remarks>
/// Publications now arrive from the supervisor, the feature lifecycle and the raid-history
/// delivery pump as well as from observation, each on whatever thread finished its work. Handlers
/// used to be invoked by every one of those threads at once, and the view model that consumes this
/// is only safe when calls reach it one at a time; in a host with no dispatcher it lost the edge
/// from InRaid to PostRaid and never produced a raid summary. Notifications are therefore
/// serialized. Publication and notification share one outer linearization gate, so a later writer
/// cannot replace the snapshot before subscribers have observed the earlier revision. The snapshot
/// is still computed under its own lock, and no handler runs while that inner lock is held.
/// </remarks>
public sealed class RuntimeStateStore : IRuntimeStateStore
{
    private readonly object _gate = new();
    private readonly object _notificationGate = new();
    private ApplicationRuntimeSnapshot _current;
    private EventHandler? _changed;
    private long _subscriberFaults;
    private bool _publicationInProgress;

    public RuntimeStateStore(RuntimeOptions options, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        _current = Freeze(new(
            options.DemoMode,
            options.IsOffline,
            false,
            new(DataAvailability.Unavailable, 0, 0, null, "No local game data"),
            null,
            new(
                null,
                RaidLifecycleState.Unknown,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                Confidence.Unknown,
                null,
                [],
                false),
            options.DemoMode
                ? new(
                    true,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Confidence.Unknown,
                    now,
                    "demo-fixture",
                    "Deterministic demo scan fixture is ready; no live game pixels are used.")
                // Not "no provider is configured": local OCR is composed and initialised on
                // Windows, and the self-test confirms it. Nothing has been scanned yet, and
                // saying otherwise told the player their installation was broken.
                // Deliberately unavailable until startup asks the recogniser whether it can
                // actually run. Claiming ready before anything has been checked would be a
                // guess, and this is replaced within a second of the window appearing.
                : ScanExecutionResult.Unavailable(
                    "Checking whether the scanner can run…",
                    now)));
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public ApplicationRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_notificationGate)
        {
            // Monitor locks are reentrant. Without an explicit guard, a subscriber that calls
            // Update can replace the snapshot and recursively notify the subscriber list before
            // later subscribers have observed the original revision. Subscribers are observers;
            // reject their nested write and let the outer publication's exception isolation count
            // that callback fault without corrupting notification order.
            if (_publicationInProgress)
            {
                throw new InvalidOperationException("A runtime state update cannot be nested inside another publication.");
            }

            _publicationInProgress = true;
            try
            {
                EventHandler[] handlers;
                lock (_gate)
                {
                    var proposed = update(_current)
                        ?? throw new InvalidOperationException("A runtime state update cannot return null.");
                    _current = Freeze(proposed with
                    {
                        LocalRevision = checked(_current.LocalRevision + 1),
                        Resources = proposed.Resources with { StateSubscriberFaults = _subscriberFaults },
                    });
                    handlers = _changed?.GetInvocationList().Cast<EventHandler>().ToArray() ?? [];
                }

                var failures = 0;
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                        failures++;
                    }
                }

                if (failures == 0)
                {
                    return;
                }

                lock (_gate)
                {
                    _subscriberFaults = checked(_subscriberFaults + failures);
                    _current = Freeze(_current with
                    {
                        LocalRevision = checked(_current.LocalRevision + 1),
                        Resources = _current.Resources with { StateSubscriberFaults = _subscriberFaults },
                    });
                }
            }
            finally
            {
                _publicationInProgress = false;
            }
        }
    }

    private static ApplicationRuntimeSnapshot Freeze(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Data);
        ArgumentNullException.ThrowIfNull(snapshot.Raid);
        ArgumentNullException.ThrowIfNull(snapshot.Scan);
        ArgumentNullException.ThrowIfNull(snapshot.Observation);
        ArgumentNullException.ThrowIfNull(snapshot.Squad);
        ArgumentNullException.ThrowIfNull(snapshot.FleaSales);
        ArgumentNullException.ThrowIfNull(snapshot.Group);
        ArgumentNullException.ThrowIfNull(snapshot.Lifecycle);
        ArgumentNullException.ThrowIfNull(snapshot.Supervisor);
        ArgumentNullException.ThrowIfNull(snapshot.Outbox);
        ArgumentNullException.ThrowIfNull(snapshot.Resources);

        return snapshot with
        {
            Profile = snapshot.Profile is null ? null : FreezeProfile(snapshot.Profile),
            Raid = snapshot.Raid with
            {
                ActiveExtracts = snapshot.Raid.ActiveExtracts.ToImmutableArray(),
                PositionTrail = snapshot.Raid.PositionTrail.ToImmutableArray(),
                ExtractLinesNotMatched = snapshot.Raid.ExtractLinesNotMatched.ToImmutableArray(),
                Transits = snapshot.Raid.Transits.ToImmutableArray(),
                Hud = snapshot.Raid.Hud is null
                    ? null
                    : snapshot.Raid.Hud with { Bars = snapshot.Raid.Hud.Bars.ToImmutableArray() },
            },
            Squad = snapshot.Squad with
            {
                Members = [.. snapshot.Squad.Members.Select(member => member with
                {
                    Equipment = member.Equipment.ToImmutableArray(),
                })],
            },
            RecentScreenshotNames = snapshot.RecentScreenshotNames.ToImmutableArray(),
            FleaSales = snapshot.FleaSales with { Sales = snapshot.FleaSales.Sales.ToImmutableArray() },
            Group = snapshot.Group with
            {
                Members = [.. snapshot.Group.Members.Select(member => member with
                {
                    Loadout = member.Loadout.ToImmutableArray(),
                    Quests = member.Quests.ToImmutableArray(),
                    Extracts = member.Extracts.ToImmutableArray(),
                    Transits = member.Transits.ToImmutableArray(),
                    QuestIds = member.QuestIds.ToImmutableArray(),
                    Trail = member.Trail.ToImmutableArray(),
                })],
                Waypoints = snapshot.Group.Waypoints.ToImmutableArray(),
                Pings = snapshot.Group.Pings.ToImmutableArray(),
                MyLoadout = snapshot.Group.MyLoadout.ToImmutableArray(),
            },
            Lifecycle = snapshot.Lifecycle with
            {
                Features = snapshot.Lifecycle.Features.IsDefault
                    ? []
                    : [.. snapshot.Lifecycle.Features.Select(feature => feature with
                    {
                        Dependencies = feature.Dependencies.IsDefault ? [] : [.. feature.Dependencies],
                    })],
            },
            Supervisor = snapshot.Supervisor with
            {
                Operations = snapshot.Supervisor.Operations.IsDefault ? [] : [.. snapshot.Supervisor.Operations],
            },
            Outbox = snapshot.Outbox with
            {
                DeadLetters = snapshot.Outbox.DeadLetters.IsDefault ? [] : [.. snapshot.Outbox.DeadLetters],
            },
        };
    }

    private static PlayerProfile FreezeProfile(PlayerProfile profile) => profile with
    {
        TraderLevels = profile.TraderLevels.ToImmutableDictionary(StringComparer.Ordinal),
        CompletedTaskIds = profile.CompletedTaskIds.ToImmutableHashSet(StringComparer.Ordinal),
        ObjectiveProgress = profile.ObjectiveProgress.ToImmutableDictionary(StringComparer.Ordinal),
        HideoutStationLevels = profile.HideoutStationLevels.ToImmutableDictionary(StringComparer.Ordinal),
        WishlistItemIds = profile.WishlistItemIds.ToImmutableHashSet(StringComparer.Ordinal),
        OwnedItemCounts = profile.OwnedItemCounts.ToImmutableDictionary(StringComparer.Ordinal),
        EventItemStates = profile.EventItemStates.ToImmutableDictionary(StringComparer.Ordinal),
        ItemOverrides = profile.ItemOverrides.ToImmutableDictionary(StringComparer.Ordinal),
    };
}

public sealed record RuntimeOptions(
    bool DemoMode,
    bool Offline,
    GameMode GameMode,
    string Language,
    TimeSpan DataFreshFor,
    TimeSpan RefreshTimeout)
{
    /// <summary>
    /// Reads a process-level offline switch that can change while the application is running.
    /// </summary>
    /// <remarks>
    /// <see cref="Offline"/> remains the deterministic fallback used by tests and fixed command
    /// line configuration. Production supplies this probe for the environment-controlled mode;
    /// snapshotting that value at composition time made reconnect require a full restart.
    /// </remarks>
    public Func<bool>? OfflineProbe { get; init; }

    /// <summary>The longest an in-process offline-to-online change waits to be observed.</summary>
    /// <remarks>
    /// This is deliberately separate from the HTTP retry delay. The runtime coordinator owns
    /// normalized data, projections and the published application state; noticing only inside
    /// the HTTP cache could download newer JSON without ever making that data visible until the
    /// next process start. A short, injected-clock interval keeps that ownership explicit and
    /// makes the transition deterministic in tests.
    /// </remarks>
    public TimeSpan OfflineTransitionPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool IsOffline => OfflineProbe?.Invoke() ?? Offline;
}

public interface IRuntimeDataStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task SeedDemoAsync(CancellationToken cancellationToken);

    Task<CachedDataSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken);
}
