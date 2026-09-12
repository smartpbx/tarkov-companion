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
                : ScanExecutionResult.Unavailable(
                    "Nothing has been scanned yet. Press the scan shortcut while the game is in front of you.",
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
