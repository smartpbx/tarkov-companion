using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;

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
    string Detail);

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
            _ when selected is not null && recommendation is null =>
                $"Resolved {selected.DisplayName}; no recommendation, because the item-context evidence it needs was not there. No pixels were persisted.",
            _ when selected is not null =>
                $"Resolved {selected.DisplayName}; no pixels were persisted.",
            _ =>
                $"{outcome.Context} scan finished with {outcome.Status}; nothing was auto-selected and no pixels were persisted.",
        };

        return new(
            outcome.Status != ScanCompletionStatus.Unavailable,
            selected is not null,
            selected?.CanonicalId,
            selected?.DisplayName,
            recommendation?.SelectedEconomicValue,
            recommendation?.ValuePerSlot,
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
    public EftObservationState Observation { get; init; } =
        OperatingSystem.IsWindows() ? EftObservationState.Idle : EftObservationState.Unsupported;

    /// <summary>The player's party, as the game's own group notifications describe it.</summary>
    public SquadSnapshot Squad { get; init; } = SquadSnapshot.Empty;

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

public sealed class RuntimeStateStore : IRuntimeStateStore
{
    private readonly object _gate = new();
    private ApplicationRuntimeSnapshot _current;

    public RuntimeStateStore(RuntimeOptions options, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        _current = new(
            options.DemoMode,
            options.Offline,
            false,
            new(DataAvailability.Unavailable, 0, 0, null, "No local game data is available."),
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
                    now));
    }

    public event EventHandler? Changed;

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
        lock (_gate)
        {
            _current = update(_current);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record RuntimeOptions(
    bool DemoMode,
    bool Offline,
    GameMode GameMode,
    string Language,
    TimeSpan DataFreshFor,
    TimeSpan RefreshTimeout);

public interface IRuntimeDataStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task SeedDemoAsync(CancellationToken cancellationToken);

    Task<CachedDataSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken);
}
