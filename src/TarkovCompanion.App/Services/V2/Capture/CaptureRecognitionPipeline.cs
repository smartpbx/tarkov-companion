using System.Security.Cryptography;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// The production analysis seam #271 left for the composition owner: classifies a captured frame
/// with the same OCR-anchor detector the V1 scanner uses, and reports the answer as a V2
/// <see cref="RecognizedContext"/> instead of running a second recognizer.
/// </summary>
/// <remarks>
/// V1's detector distinguishes four screen shapes (single item, container, extract list, flea),
/// while V2 intents distinguish five container-shaped screens (loot, stash, ammo, keys, quest
/// items). Telling those apart from OCR anchors alone is #273's unresolved recognition work, out
/// of scope for this pass. When a generic container is detected, this pipeline trusts the intent
/// the player already armed rather than guessing a specific one, and never claims a live
/// detection it can't support: an unrecognised or ambiguous screen reports null context, still
/// requiring the reviewer's confirmation before anything is accepted (#271's review stage).
/// </remarks>
/// <remarks>
/// #273's grid reconstruction runs here too, and only here: pixels are released once analysis
/// returns (<c>CaptureSessionCoordinator</c> is pixel-free past this point), so this is the one
/// place a grid-shaped intent can still be turned into a <see cref="GridReconstructionRequest"/>.
/// The request is itself pixel-free (bounds and evidenced values, never raw bytes), so it can
/// safely ride along on <see cref="CaptureAnalysis"/> to the handoff.
/// </remarks>
public sealed class CaptureRecognitionPipeline(
    OcrCoordinator ocr,
    GridPixelReconstructionBuilder gridBuilder,
    TimeProvider? timeProvider = null,
    // [V2 rough package 60 — Intel scan] #287: the catalog resolver, so a single-item screen
    // leaves this pipeline knowing WHICH item it showed. Optional so a host that composes the
    // pipeline without a catalog still builds; it then identifies nothing rather than guessing.
    CanonicalItemResolverCache? resolverCache = null,
    OcrTextNormalizer? normalizer = null,
    // [f920 capture] #284: the flea row parser V1 already had. Optional and last: where OCR is
    // not composed (every platform but Windows) there is nothing to parse rows from.
    TarkovCompanion.Core.Abstractions.IFleaRecognitionService? flea = null,
    // #572: correlated by CaptureAnalysisRequest.CorrelationId, not threaded through
    // CaptureAnalysis - see ICaptureStageTimeline's own remarks for why. Optional: every existing
    // composition and test predates it, and a host that never registers one gets no timing lines
    // rather than a missing-service failure.
    Application.Services.CaptureSessions.ICaptureStageTimeline? stageTimeline = null) : ICaptureSessionPipeline, ILootScanRecognitionProgressSource
{
    private readonly OcrCoordinator _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
    private readonly GridPixelReconstructionBuilder _gridBuilder = gridBuilder ?? throw new ArgumentNullException(nameof(gridBuilder));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly CanonicalItemResolverCache? _resolverCache = resolverCache;
    private readonly OcrTextNormalizer _normalizer = normalizer ?? new OcrTextNormalizer();

    public event EventHandler<LootScanRecognitionStarted>? LootRecognitionStarted;

    public event EventHandler<LootScanItemMatched>? LootItemMatched;

    public event EventHandler<LootScanRecognitionStopped>? LootRecognitionStopped;

    public async Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Content identity travels with the analysis rather than the pixels themselves, so a
        // pixel-free downstream consumer (the handoff) can still bind advice to the exact frame
        // that produced it.
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(request.Image.Pixels.Span));
        var progressStarted = false;

        void StartLootProgress()
        {
            if (progressStarted)
            {
                return;
            }

            progressStarted = true;
            LootRecognitionStarted?.Invoke(this, new(
                request.SessionId,
                request.ArtifactId,
                request.CorrelationId,
                request.DecodeRevision,
                request.Context,
                contentHash,
                _timeProvider.GetUtcNow()));
        }

        // An armed Loot capture is known before OCR. An unarmed Auto capture becomes loot only
        // after the context detector and measured lattice place it below.
        if (request.RequestedIntent == ScanIntent.Loot)
        {
            StartLootProgress();
        }

        try
        {
            var ocrStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var coordinated = await _ocr.RecognizeAsync(request.Image, cancellationToken).ConfigureAwait(false);
            stageTimeline?.Mark(request.CorrelationId, "context_ocr", ocrStopwatch.Elapsed);
            var detection = coordinated.Detection;
            var isAmbiguous = detection.Context == ScanContext.Unknown;
            var detectedContext = isAmbiguous ? (RecognizedContext?)null : Map(detection.Context, request.RequestedIntent);
            var isAvailable = !coordinated.IsEmpty && coordinated.FullFrame.IsAvailable;

            GridReconstructionRequest? grid = null;
            GridReconstructionRequest? carried = null;
            IReadOnlyList<CarriedGridReconstructionRequest> carriedGrids = [];
            var carriedCoverageComplete = false;
            if (GridSurfaceFor(request.RequestedIntent, detection.Context, request.Context.ActiveMap is not null) is { } surface)
            {
                if (surface == InventoryGridSurface.VisibleLoot)
                {
                    StartLootProgress();
                }

                var gridStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var progress = surface == InventoryGridSurface.VisibleLoot
                    ? new InlineProgress<GridCellObservation>(cell => LootItemMatched?.Invoke(this, new(
                        request.SessionId,
                        request.ArtifactId,
                        request.CorrelationId,
                        request.DecodeRevision,
                        cell)))
                    : null;
                grid = await _gridBuilder
                    .BuildAsync(
                        request.Image,
                        surface,
                        _timeProvider.GetUtcNow(),
                        cancellationToken: cancellationToken,
                        matchedItemProgress: progress)
                    .ConfigureAwait(false);
            // The in-raid Gear screen shows the player's backpack beside the loot. Reading it is
            // what lets the Loot Scan say where an item goes, or what to drop for it, instead of
            // "TAKE?" with the carried grid unread.
                if (surface == InventoryGridSurface.VisibleLoot)
                {
                    carriedGrids = await _gridBuilder
                        .BuildCarriedGridsAsync(request.Image, _timeProvider.GetUtcNow(), cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    carried = carriedGrids
                        .FirstOrDefault(candidate => candidate.Identity == CarriedGridIdentity.PrimaryBackpack)
                        ?.Reconstruction;
                    // Pockets anchor the carried column and the backpack anchors the lower extent.
                    // Without either, a known grid may still prove a fit but cannot prove no fit.
                    carriedCoverageComplete =
                        carriedGrids.Any(candidate => candidate.Identity.Kind == CarriedGridKind.Backpack) &&
                        carriedGrids.Any(candidate => candidate.Identity.Kind == CarriedGridKind.Pockets);
                }
            // Region detection and per-cell icon matching against the catalog both happen inside
            // BuildAsync; splitting them would mean Infrastructure taking a dependency on this
            // Application-layer timeline, so they are reported together here as one stage.
                stageTimeline?.Mark(request.CorrelationId, "grid_and_icon_matching", gridStopwatch.Elapsed);
            }

        // A measured lattice is a usable reading whether or not any text was read. Availability
        // was OCR's alone, so a Loot or Stash frame on a machine with no OCR engine - or one whose
        // text simply was not legible - resolved its review and then ended "no change", with the
        // grid it had measured thrown away.
            isAvailable |= grid?.Lattice is not null;

        // The text detector could not place the screen, the player armed Loot or Stash, and the
        // pixels hold a measured inventory lattice: that is a grid screen, placed from its lines
        // instead of its words. Without this the review offered "Analyse as armed" and intake
        // then refused it as "context unknown, no change" - the button did nothing, on the one
        // machine class (no OCR, or OCR that read no anchor) where it was the only way forward.
            var fleaListings = await ReadFleaListingsAsync(request, detection.Context, cancellationToken).ConfigureAwait(false);
            if (fleaListings.Count > 0 && isAmbiguous && request.RequestedIntent == ScanIntent.Flea)
            {
            // Priced rows under an armed Flea intent are a flea screen, whatever the anchor
            // detector made of the header.
                detectedContext = RecognizedContext.Flea;
                isAmbiguous = false;
            }

            (detectedContext, isAmbiguous, var confidence) = PlaceFromLattice(
                detectedContext,
                isAmbiguous,
                detection.Confidence,
                grid?.Lattice is not null,
                request.RequestedIntent);

            return new(
                contentHash,
                detectedContext,
                isAmbiguous,
                isAvailable,
                coordinated.DiagnosticCode,
                confidence,
                grid,
                await IdentifyAsync(coordinated, detectedContext, request.RequestedIntent, cancellationToken)
                    .ConfigureAwait(false),
                CarriedGrid: carried,
                FleaListings: fleaListings,
                CarriedGrids: carriedGrids,
                CarriedCoverageComplete: carriedCoverageComplete);
        }
        catch (OperationCanceledException)
        {
            if (progressStarted)
            {
                LootRecognitionStopped?.Invoke(this, new(
                    request.SessionId,
                    request.ArtifactId,
                    request.CorrelationId,
                    request.DecodeRevision,
                    WasCancelled: true));
            }

            throw;
        }
        catch
        {
            if (progressStarted)
            {
                LootRecognitionStopped?.Invoke(this, new(
                    request.SessionId,
                    request.ArtifactId,
                    request.CorrelationId,
                    request.DecodeRevision,
                    WasCancelled: false));
            }

            throw;
        }
    }

    /// <summary>
    /// <see cref="Progress{T}"/> captures a synchronization context. Recognition must report on
    /// the worker that completed a cell; the UI bridge owns the one dispatcher hop.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// The visible rows of a flea screen, parsed by the same service V1 uses.
    /// </summary>
    /// <remarks>
    /// Run for an armed Flea intent, or when the anchor detector says the screen is the flea. It
    /// reads the picture the player took and nothing else. A parser that is not composed, a
    /// provider that is unavailable or a deadline that expired all give no rows, which the
    /// handoff reports as "no rows were legible" rather than as an empty market.
    /// </remarks>
    private async Task<IReadOnlyList<CaptureFleaListing>> ReadFleaListingsAsync(
        CaptureAnalysisRequest request,
        ScanContext detected,
        CancellationToken cancellationToken)
    {
        if (flea is null || !ReadsFleaRows(request.RequestedIntent, detected))
        {
            return [];
        }

        try
        {
            var read = await flea.RecognizeAsync(request.Image, cancellationToken).ConfigureAwait(false);
            return
            [
                .. read.Listings
                    .OrderBy(listing => listing.Bounds.Y)
                    .Select(listing => new CaptureFleaListing(
                        listing.PriceRoubles,
                        listing.Quantity,
                        listing.Confidence,
                        listing.SourceText,
                        listing.CurrencyCode,
                        listing.OriginalPrice,
                        listing.CurrencyRateRoubles,
                        listing.CurrencyRateProvenance,
                        listing.Condition)),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    internal static bool ReadsFleaRows(ScanIntent intent, ScanContext detected) =>
        intent == ScanIntent.Flea || (intent == ScanIntent.Auto && detected == ScanContext.FleaListings);

    /// <summary>
    /// What single item this frame showed, when the screen is one that holds a single item.
    /// </summary>
    /// <remarks>
    /// Resolved from the lines the coordinator already read rather than by asking a second
    /// recognizer, which would double the slowest step of a capture and let two code paths
    /// disagree about the same screenshot. Only item-shaped screens are resolved: running the
    /// fuzzy catalog search over a stash grid's hundreds of lines is work whose answer nothing
    /// uses, and the grid lattice is the right reading of that frame.
    ///
    /// A resolver that never loaded, or a deadline that expired mid-search, identifies nothing.
    /// Both are honest: the review still says what the recognizer was sure of, and the player
    /// still has Retry.
    /// </remarks>
    private async Task<IReadOnlyList<CaptureIdentifiedItem>> IdentifyAsync(
        CoordinatedOcrResult coordinated,
        RecognizedContext? detectedContext,
        ScanIntent requestedIntent,
        CancellationToken cancellationToken)
    {
        if (_resolverCache is null || !IdentifiesItems(detectedContext, requestedIntent))
        {
            return [];
        }

        try
        {
            var resolver = await _resolverCache.GetAsync(cancellationToken).ConfigureAwait(false);
            return
            [
                .. OcrItemCandidates
                    .Rank(coordinated.Candidates, resolver, _normalizer, cancellationToken)
                    .Select(candidate => new CaptureIdentifiedItem(
                        candidate.CanonicalId,
                        candidate.DisplayName,
                        candidate.Confidence,
                        candidate.Evidence)),
            ];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Whether this screen is one whose answer is an item rather than a lattice.</summary>
    /// <remarks>
    /// The detected context decides when there is one. When there is not — an unreadable or
    /// ambiguous screen — only Auto tries anyway, because a player who armed Auto and pressed the
    /// shutter asked "what is this", which is a question worth attempting on a frame the detector
    /// could not place. An armed Stash or Loot on an unreadable frame is a different question and
    /// a catalog search of its lines would answer neither.
    /// </remarks>
    internal static bool IdentifiesItems(RecognizedContext? detectedContext, ScanIntent requestedIntent) =>
        detectedContext is RecognizedContext.Item
        // A flea search shows one item's offers, and its name is on every row.
        || detectedContext is RecognizedContext.Flea
        || requestedIntent is ScanIntent.Flea
        || (detectedContext is null && requestedIntent is ScanIntent.Auto);

    /// <summary>
    /// Which lattice a grid-shaped intent's screen measures. Ammo/Keys/Quest-items grids are other
    /// packages' rough pass (their capture handoffs do not exist yet), so only the two intents
    /// this package wires - Loot and Stash - request reconstruction here.
    /// </summary>
    internal static InventoryGridSurface? GridSurfaceFor(ScanIntent intent) => intent switch
    {
        ScanIntent.Loot => InventoryGridSurface.VisibleLoot,
        ScanIntent.Stash => InventoryGridSurface.Stash,
        // #283: an Ammo or Keys sub-scan is a screenshot of an open case; the case window is read.
        ScanIntent.Ammo or ScanIntent.Keys => InventoryGridSurface.Container,
        _ => null,
    };

    /// <summary>
    /// Places a screen the text detector could not, from the lattice measured in its pixels.
    /// </summary>
    /// <remarks>
    /// Only under an armed Loot or Stash, where the player has said what the screen is and the
    /// lattice bears them out. The confidence is the floor at which intake will act on a reading,
    /// never the "auto-selected" band: the lines were measured, the words were not read.
    /// </remarks>
    internal static (RecognizedContext? Context, bool IsAmbiguous, Confidence Confidence) PlaceFromLattice(
        RecognizedContext? detected,
        bool isAmbiguous,
        Confidence confidence,
        bool latticeMeasured,
        ScanIntent intent) =>
        isAmbiguous && latticeMeasured && GridSurfaceFor(intent) is not null
            ? (MapContainer(intent), false, new(Math.Max(confidence.Value, RecognitionThresholds.Ambiguous)))
            : (detected, isAmbiguous, confidence);

    /// <summary>
    /// The same, for a frame nobody armed an intent for.
    /// </summary>
    /// <remarks>
    /// An armed intent lasts thirty seconds, and nobody looting a container alt-tabs to arm one
    /// first. An unarmed screenshot of a container was detected as a grid, handed to the Loot
    /// Scan as its effective intent, and arrived there with no grid at all, because only an armed
    /// Loot or Stash asked for one: the page showed "no usable result" for the one capture the
    /// product is for. During a raid a container screen is loot, so its lattice is measured.
    /// Outside one it may be the stash, a trader or a case, and the armed intent still decides.
    /// </remarks>
    internal static InventoryGridSurface? GridSurfaceFor(ScanIntent intent, ScanContext detected, bool inRaid) =>
        GridSurfaceFor(intent) ??
        (intent == ScanIntent.Auto && detected == ScanContext.Container && inRaid
            ? InventoryGridSurface.VisibleLoot
            : null);

    internal static RecognizedContext Map(ScanContext context, ScanIntent requestedIntent) => context switch
    {
        ScanContext.SingleItem => RecognizedContext.Item,
        ScanContext.ExtractList => RecognizedContext.ExtractsAndMap,
        ScanContext.FleaListings => RecognizedContext.Flea,
        ScanContext.Container => MapContainer(requestedIntent),
        _ => RecognizedContext.Grid,
    };

    /// <summary>
    /// V1's container context covers loot, stash, ammo, keys, and quest-item grids alike. Until
    /// #273 tells them apart from pixels, the armed intent is the only honest signal available.
    /// </summary>
    internal static RecognizedContext MapContainer(ScanIntent requestedIntent) => requestedIntent switch
    {
        ScanIntent.Loot => RecognizedContext.Loot,
        ScanIntent.Stash => RecognizedContext.Stash,
        ScanIntent.Ammo => RecognizedContext.Ammo,
        ScanIntent.Keys => RecognizedContext.Keys,
        ScanIntent.QuestItems => RecognizedContext.QuestItems,
        _ => RecognizedContext.Grid,
    };
}
