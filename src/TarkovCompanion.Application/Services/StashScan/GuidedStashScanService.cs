using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

public enum GuidedStashScanStage
{
    Idle,
    Collecting,
}

/// <summary>What happened to the screenshot that was just offered to a guided scan.</summary>
public enum GuidedStashFrameOutcome
{
    Added,
    AddedUnplaced,
    AddedNoNewRows,
    Duplicate,
    NoGrid,
    NotCollecting,
}

/// <summary>An unfinished guided scan as it is kept between runs of the application: no pixels.</summary>
public sealed record GuidedStashScanPending(
    Guid SessionId,
    DateTimeOffset StartedUtc,
    Guid ProfileId,
    string ProfileGeneration,
    string GameMode,
    string DataSnapshotId,
    RecognitionResultEnvelope<StashRecognition>? Recognition,
    StashScanKind Kind = StashScanKind.Full);

/// <summary>Where an unfinished guided scan waits while the application is closed.</summary>
public interface IGuidedStashScanPendingStore
{
    Task<GuidedStashScanPending?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(GuidedStashScanPending pending, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>The guided scan as the workspace shows it.</summary>
public sealed record GuidedStashScanProgress(
    GuidedStashScanStage Stage,
    DateTimeOffset? StartedUtc,
    bool WasResumed,
    int Screenshots,
    int PlacedScreenshots,
    int RowsCovered,
    string Headline,
    string NextStep,
    StashReconstruction Reconstruction)
{
    /// <summary>A full scroll-through, or an Ammo or Keys case sub-scan.</summary>
    public StashScanKind Kind { get; init; } = StashScanKind.Full;

    public static GuidedStashScanProgress Idle { get; } = new(
        GuidedStashScanStage.Idle,
        null,
        false,
        0,
        0,
        0,
        "No scan in progress",
        "Start a full scan, scroll to the top of your stash, and take a screenshot.",
        StashReconstruction.Empty);

    public bool IsCollecting => Stage == GuidedStashScanStage.Collecting;
}

/// <summary>What finishing a guided scan produced.</summary>
public sealed record GuidedStashScanFinished(
    StashScanAssemblyResult Assembly,
    StashReconstruction Reconstruction,
    StashOwnedCountsChange OwnedCounts)
{
    public StashScanKind Kind { get; init; } = StashScanKind.Full;

    /// <summary>What the scan did to the owned counts, in the player's terms.</summary>
    public string Summary => Kind != StashScanKind.Full && OwnedCounts == StashOwnedCountsChange.None
        ? $"No {(Kind == StashScanKind.Keys ? "keys" : "ammo")} named, so owned counts are unchanged."
        : OwnedCounts.Summary;
}

/// <summary>
/// One stash, several screenshots: holds the frames of a scroll-through until the player says the
/// stash is done, and says after each one what to do next.
/// </summary>
/// <remarks>
/// <para>
/// Before this, every accepted Stash capture became its own one-frame snapshot and replaced the
/// last as "current". A stash is taller than a screen - three screens for 34 rows at 1080p - so
/// the workspace only ever showed the last screenful, and arming expired after thirty seconds, so
/// each screenshot needed a trip back to the capture dialog.
/// </para>
/// <para>
/// A session is never dropped quietly. It is written to the pending store after every screenshot,
/// without pixels, and read back on the next start: closing the application halfway leaves the
/// scan waiting, and it ends only by Finish or an explicit Discard. A screenshot that does not
/// line up is kept rather than refused, because a later one can bridge the gap, and the last
/// screenshot can be taken back.
/// </para>
/// </remarks>
public sealed class GuidedStashScanService(
    StashScanAssembler assembler,
    StashLayoutAligner aligner,
    StashReconstructionProjector projector,
    StashScanWorkflow workflow,
    StashOwnedCountsApplier ownedCounts,
    IGuidedStashScanPendingStore pendingStore,
    TimeProvider? timeProvider = null,
    IItemFactCatalog? itemFacts = null)
{
    private const string RootContainer = "stash";

    private static readonly ProducerIdentity Producer = new("Tarkov Companion guided stash scan", "guided-stash-scan-1");

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<StashScanCaptureFrame> _frames = [];
    private GuidedStashScanPending? _pending;
    private bool _resumed;
    private GuidedStashScanProgress _current = GuidedStashScanProgress.Idle;

    public event EventHandler? Changed;

    public GuidedStashScanProgress Current => Volatile.Read(ref _current);

    /// <summary>Reads back a scan that was left unfinished, if there is one.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is not null || await pendingStore.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } pending)
            {
                return;
            }

            _pending = pending;
            _resumed = true;
            _frames.Clear();
            _frames.AddRange(FramesFrom(pending));
            Publish(Describe(GuidedStashFrameOutcome.Added, previousRows: 0));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a scan, or returns the one already in progress rather than replacing it.</summary>
    public async Task<GuidedStashScanProgress> StartAsync(
        InventoryProfileScope scope,
        string dataSnapshotId,
        CancellationToken cancellationToken,
        StashScanKind kind = StashScanKind.Full)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is not null)
            {
                return Current;
            }

            _pending = new(
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                scope.ProfileId,
                scope.Generation,
                scope.GameMode,
                dataSnapshotId,
                null,
                kind);
            _resumed = false;
            _frames.Clear();
            await pendingStore.SaveAsync(_pending, cancellationToken).ConfigureAwait(false);
            Publish(Describe(GuidedStashFrameOutcome.Added, previousRows: 0));
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Offers one reviewed stash screenshot to the scan in progress.</summary>
    public async Task<GuidedStashFrameOutcome> AddScreenshotAsync(
        string artifactId,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        string contentSha256,
        DateTimeOffset capturedUtc,
        int decodeRevision,
        GridReconstructionResult reconstruction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is null)
            {
                return GuidedStashFrameOutcome.NotCollecting;
            }

            if (reconstruction.Recognition is null)
            {
                Publish(Describe(GuidedStashFrameOutcome.NoGrid, Current.RowsCovered));
                return GuidedStashFrameOutcome.NoGrid;
            }

            if (_frames.Any(frame =>
                    string.Equals(frame.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(frame.ArtifactId, artifactId, StringComparison.Ordinal)))
            {
                Publish(Describe(GuidedStashFrameOutcome.Duplicate, Current.RowsCovered));
                return GuidedStashFrameOutcome.Duplicate;
            }

            var observedUtc = _timeProvider.GetUtcNow();
            var captured = capturedUtc.ToUniversalTime();
            var provenance = new EvidenceProvenance(
                EvidenceSourceClass.GameWrittenScreenshot,
                "recognition.stash.capture",
                observedUtc < captured ? captured : observedUtc,
                EvidenceConfidence.Unscored,
                Producer);
            var ordinal = _frames.Count == 0 ? 0 : _frames.Max(frame => frame.CaptureOrdinal) + 1;
            var subScan = _pending.Kind != StashScanKind.Full;
            _frames.Add(new StashScanCaptureFrame(
                new CaptureSessionId(_pending.SessionId),
                artifactId,
                ordinal,
                correlationId,
                context,
                contentSha256,
                subScan ? StashSubScan.CasePath(_pending.Kind, ordinal) : RootContainer,
                captured,
                decodeRevision,
                provenance,
                reconstruction,
                UnknownTotal(provenance),
                // Every case is its own container, whole on its screenshot: nothing to stitch.
                confirmsContainerStart: ordinal == 0 || subScan));

            var previousRows = Current.RowsCovered;
            var previousPlaced = Current.PlacedScreenshots;
            var assembly = Assemble(_pending, _frames);
            _pending = _pending with { Recognition = assembly.Recognition };
            await pendingStore.SaveAsync(_pending, cancellationToken).ConfigureAwait(false);

            var reconstructionNow = projector.Project(assembly.Recognition.Result.Value!);
            var placed = _frames.Count - reconstructionNow.UnplacedRegions;
            var outcome = subScan
                ? GuidedStashFrameOutcome.Added
                : placed <= previousPlaced
                ? GuidedStashFrameOutcome.AddedUnplaced
                : Rows(reconstructionNow) <= previousRows && ordinal > 0
                    ? GuidedStashFrameOutcome.AddedNoNewRows
                    : GuidedStashFrameOutcome.Added;
            Publish(Describe(outcome, previousRows, reconstructionNow));
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes back the last screenshot: the answer to one taken in the wrong place.</summary>
    public async Task UndoLastAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is null || _frames.Count == 0)
            {
                return;
            }

            _frames.RemoveAt(_frames.Count - 1);
            _pending = _pending with
            {
                Recognition = _frames.Count == 0 ? null : Assemble(_pending, _frames).Recognition,
            };
            await pendingStore.SaveAsync(_pending, cancellationToken).ConfigureAwait(false);
            Publish(Describe(GuidedStashFrameOutcome.Added, previousRows: 0));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves what was scanned as the current stash snapshot and applies its counts. Returns null
    /// when there is nothing to save; the scan then stays open.
    /// </summary>
    public async Task<GuidedStashScanFinished?> FinishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is null || _frames.Count == 0)
            {
                return null;
            }

            var kind = _pending.Kind;
            var request = Request(_pending, Aligned(_pending, _frames));
            // A case scan is saved beside the stash snapshot, never in its place: it saw one case.
            var assembly = await workflow
                .CompleteAsync(request, Guid.NewGuid(), makeCurrent: kind == StashScanKind.Full, cancellationToken)
                .ConfigureAwait(false);
            var reconstruction = projector.Project(assembly.Recognition.Result.Value!);
            var change = kind == StashScanKind.Full
                ? await ownedCounts.ApplyAsync(reconstruction, cancellationToken).ConfigureAwait(false)
                : await ownedCounts.ApplyRaisingAsync(
                        reconstruction,
                        await StashSubScan.ItemsOfAsync(kind, itemFacts, cancellationToken).ConfigureAwait(false),
                        cancellationToken)
                    .ConfigureAwait(false);

            var finished = new GuidedStashScanFinished(assembly, reconstruction, change) { Kind = kind };

            // Only now, with the snapshot durably saved, does the pending copy go.
            await pendingStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            _pending = null;
            _resumed = false;
            _frames.Clear();
            Publish(GuidedStashScanProgress.Idle with
            {
                Headline = StashSubScan.SavedHeadline(kind),
                NextStep = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{reconstruction.KnownTiles} named, {reconstruction.UnknownTiles} unknown. {finished.Summary}"),
            });
            return finished;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Throws the scan in progress away. Nothing else does.</summary>
    public async Task DiscardAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pendingStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            _pending = null;
            _resumed = false;
            _frames.Clear();
            Publish(GuidedStashScanProgress.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StashScanAssemblyResult Assemble(GuidedStashScanPending pending, IReadOnlyList<StashScanCaptureFrame> frames) =>
        assembler.Assemble(Request(pending, Aligned(pending, frames)));

    /// <summary>Identity alignment first; layout alignment only for what that left unplaced.</summary>
    private IReadOnlyList<StashScanCaptureFrame> Aligned(GuidedStashScanPending pending, IReadOnlyList<StashScanCaptureFrame> frames)
    {
        if (pending.Kind != StashScanKind.Full)
        {
            return frames;
        }

        var firstPass = assembler.Assemble(Request(pending, frames));
        return aligner.AddLayoutOrigins(frames, firstPass, _timeProvider.GetUtcNow());
    }

    private StashScanAssemblyRequest Request(GuidedStashScanPending pending, IReadOnlyList<StashScanCaptureFrame> frames) => new(
        $"stash-scan-{pending.SessionId:N}",
        $"stash-snapshot-{pending.SessionId:N}",
        new CaptureSessionId(pending.SessionId),
        new InventoryProfileScope(pending.ProfileId, pending.ProfileGeneration, pending.GameMode),
        pending.DataSnapshotId,
        _timeProvider.GetUtcNow(),
        frames);

    private GuidedStashScanProgress Describe(
        GuidedStashFrameOutcome outcome,
        int previousRows,
        StashReconstruction? reconstruction = null)
    {
        if (_pending is null)
        {
            return GuidedStashScanProgress.Idle;
        }

        reconstruction ??= _pending.Recognition?.Result.Value is { } stash
            ? projector.Project(stash)
            : StashReconstruction.Empty;
        var rows = Rows(reconstruction);
        var placed = _frames.Count - reconstruction.UnplacedRegions;
        var culture = CultureInfo.CurrentCulture;
        if (_pending.Kind != StashScanKind.Full)
        {
            return new(
                GuidedStashScanStage.Collecting,
                _pending.StartedUtc,
                _resumed,
                _frames.Count,
                placed,
                rows,
                StashSubScan.Headline(_pending.Kind, _frames.Count, reconstruction),
                StashSubScan.NextStep(_pending.Kind, outcome, _frames.Count),
                reconstruction)
            {
                Kind = _pending.Kind,
            };
        }

        var headline = _frames.Count == 0
            ? "Scan started"
            : string.Create(culture, $"{_frames.Count} screenshot{(_frames.Count == 1 ? string.Empty : "s")} · rows 1–{rows}");
        var nextStep = outcome switch
        {
            GuidedStashFrameOutcome.NoGrid => "No stash grid in that screenshot. Open your stash and take it again.",
            GuidedStashFrameOutcome.Duplicate => "Same screenshot as before — skipped. Scroll down and take the next one.",
            // On a real unguided burst the player paged the stash, and a page leaves only the row
            // the viewport cuts in common: nothing to line two screens up by.
            GuidedStashFrameOutcome.AddedUnplaced => "That one shares no rows with the last — a full page is too far. Scroll back up about half a screen and take it again. It's kept in case a later one joins them.",
            GuidedStashFrameOutcome.AddedNoNewRows => "No new rows in that one. If that was the bottom of your stash, press Finish.",
            _ when _frames.Count == 0 && _resumed => "Picked up where you left off. Scroll to the top of your stash and take a screenshot.",
            _ when _frames.Count == 0 => "Scroll to the top of your stash and take a screenshot.",
            _ when _resumed && rows == previousRows => string.Create(culture, $"Picked up where you left off. Scroll so row {Math.Max(1, rows - 3)} is near the top and take the next one, or press Finish."),
            _ => "Scroll down about half a screen with the mouse wheel, not a full page, and take the next one. Press Finish at the bottom.",
        };
        return new(
            GuidedStashScanStage.Collecting,
            _pending.StartedUtc,
            _resumed,
            _frames.Count,
            placed,
            rows,
            headline,
            nextStep,
            reconstruction);
    }

    private void Publish(GuidedStashScanProgress progress)
    {
        Volatile.Write(ref _current, progress);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int Rows(StashReconstruction reconstruction) =>
        reconstruction.Containers
            .Where(container => string.Equals(container.ContainerPath, RootContainer, StringComparison.Ordinal))
            .Select(container => container.Rows)
            .DefaultIfEmpty(0)
            .Max();

    private static EvidencedValue<int?> UnknownTotal(EvidenceProvenance provenance) => new(
        "stash.capture.total-cells",
        null,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "stash.capture.total-cells.unresolved"),
        provenance);

    /// <summary>
    /// Rebuilds assembler input from the regions a pending scan kept. Each placed region carries
    /// its origin back in as a hint, so a resumed scan lands exactly where it was.
    /// </summary>
    private static IEnumerable<StashScanCaptureFrame> FramesFrom(GuidedStashScanPending pending)
    {
        if (pending.Recognition?.Result.Value is not { } stash)
        {
            yield break;
        }

        var startedUtc = pending.StartedUtc.ToUniversalTime();
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "recognition.stash.capture.resumed",
            startedUtc,
            EvidenceConfidence.Unscored,
            Producer);
        foreach (var region in stash.CapturedRegions.OrderBy(region => region.CaptureOrdinal))
        {
            var unresolved = region.Grid.Cells
                .Where(cell => cell.Item.Value is null && cell.Item.Bounds is not null)
                .Select(cell => new GridCellObservation(
                    $"cell-{cell.Anchor.Row:D3}-{cell.Anchor.Column:D3}",
                    cell.Anchor,
                    cell.Item))
                .ToArray();
            var reconstruction = new GridReconstructionResult(
                unresolved.Length == 0 ? GridReconstructionOutcome.Complete : GridReconstructionOutcome.Partial,
                InventoryGridSurface.Stash,
                region.Grid,
                unresolved,
                []);
            var isStart = (region.CaptureOrdinal == 0 || pending.Kind != StashScanKind.Full) &&
                          region.OriginInContainer.Value is { Row: 0, Column: 0 };
            yield return new StashScanCaptureFrame(
                new CaptureSessionId(pending.SessionId),
                region.ArtifactId,
                region.CaptureOrdinal,
                CaptureCorrelationId.New(),
                new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"resumed:{region.ArtifactId}"))),
                region.ContainerPath,
                startedUtc,
                0,
                provenance,
                reconstruction,
                UnknownTotal(provenance),
                confirmsContainerStart: isStart,
                originHint: isStart || region.OriginInContainer.Value is null ? null : region.OriginInContainer);
        }
    }
}
