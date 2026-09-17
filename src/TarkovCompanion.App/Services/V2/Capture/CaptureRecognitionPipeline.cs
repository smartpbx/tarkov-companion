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
    TimeProvider? timeProvider = null) : ICaptureSessionPipeline
{
    private readonly OcrCoordinator _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
    private readonly GridPixelReconstructionBuilder _gridBuilder = gridBuilder ?? throw new ArgumentNullException(nameof(gridBuilder));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Content identity travels with the analysis rather than the pixels themselves, so a
        // pixel-free downstream consumer (the handoff) can still bind advice to the exact frame
        // that produced it.
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(request.Image.Pixels.Span));

        var coordinated = await _ocr.RecognizeAsync(request.Image, cancellationToken).ConfigureAwait(false);
        var detection = coordinated.Detection;
        var isAmbiguous = detection.Context == ScanContext.Unknown;
        var detectedContext = isAmbiguous ? (RecognizedContext?)null : Map(detection.Context, request.RequestedIntent);
        var isAvailable = !coordinated.IsEmpty && coordinated.FullFrame.IsAvailable;

        GridReconstructionRequest? grid = null;
        if (GridSurfaceFor(request.RequestedIntent) is { } surface)
        {
            grid = await _gridBuilder
                .BuildAsync(request.Image, surface, _timeProvider.GetUtcNow(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new(
            contentHash,
            detectedContext,
            isAmbiguous,
            isAvailable,
            coordinated.DiagnosticCode,
            detection.Confidence,
            grid);
    }

    /// <summary>
    /// Which lattice a grid-shaped intent's screen measures. Ammo/Keys/Quest-items grids are other
    /// packages' rough pass (their capture handoffs do not exist yet), so only the two intents
    /// this package wires - Loot and Stash - request reconstruction here.
    /// </summary>
    internal static InventoryGridSurface? GridSurfaceFor(ScanIntent intent) => intent switch
    {
        ScanIntent.Loot => InventoryGridSurface.VisibleLoot,
        ScanIntent.Stash => InventoryGridSurface.Stash,
        _ => null,
    };

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
