using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Turns one reviewed Stash-intent capture into a durable stash snapshot, using #273's real grid
/// reconstruction instead of #382's placeholder empty request.
/// </summary>
/// <remarks>
/// <see cref="StashScanAssembler"/> already stitches an ordered, multi-capture session (moving
/// through container tabs, opened nested containers, origin hints) - that is capture-lifecycle UI
/// this rough pass does not attempt. Every accepted capture here becomes its own one-frame session
/// instead: the root <c>stash</c> container, confirmed to start at cell zero, with an unresolved
/// total-cell count. The assembler already reports that honestly (a partial snapshot with missing
/// coverage) rather than this adapter guessing the stash's real size; stitching several captures
/// into one wider snapshot is deferred to whichever package wires the capture-session UI for it.
/// </remarks>
public sealed class StashScanCaptureHandoff(
    IProfileRuntimeContextService profileContext,
    InventoryGridReconstructor gridReconstructor,
    StashScanWorkflow workflow,
    TimeProvider? timeProvider = null,
    ILogger<StashScanCaptureHandoff>? logger = null) : ICaptureResultHandoff
{
    private readonly IProfileRuntimeContextService _profileContext =
        profileContext ?? throw new ArgumentNullException(nameof(profileContext));
    private readonly InventoryGridReconstructor _gridReconstructor =
        gridReconstructor ?? throw new ArgumentNullException(nameof(gridReconstructor));
    private readonly StashScanWorkflow _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<StashScanCaptureHandoff> _logger = logger ?? NullLogger<StashScanCaptureHandoff>.Instance;

    public async ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EffectiveIntent != ScanIntent.Stash)
        {
            return CaptureHandoffResult.Accepted;
        }

        var snapshot = _profileContext.Current;
        if (snapshot.ActiveProfile is not { } profile)
        {
            _logger.LogInformation("Skipped a stash scan because no active profile is selected yet.");
            return CaptureHandoffResult.Accepted;
        }

        try
        {
            await CompleteAsync(request, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not assemble a stash snapshot for capture session {SessionId}.", request.SessionId);
        }

        return CaptureHandoffResult.Accepted;
    }

    private async Task CompleteAsync(CaptureHandoffRequest request, ProfileRecord profile, CancellationToken cancellationToken)
    {
        var scope = new InventoryProfileScope(
            profile.Context.Identity.ProfileId,
            profile.Context.Identity.Generation,
            profile.Context.Mode.ToString());
        var assembledUtc = _timeProvider.GetUtcNow();
        var contentHash = request.Analysis.ResultId;
        var reconstructionRequest = request.Analysis.Grid is { Surface: InventoryGridSurface.Stash } stashRequest
            ? stashRequest
            : new(InventoryGridSurface.Stash, lattice: null, occupiedCells: []);
        var reconstruction = _gridReconstructor.Reconstruct(reconstructionRequest, cancellationToken);

        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "recognition.stash.capture",
            assembledUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("Tarkov Companion stash capture handoff", "stash-capture-handoff-1"));
        var frame = new StashScanCaptureFrame(
            request.SessionId,
            request.ArtifactId,
            captureOrdinal: 0,
            request.CorrelationId,
            request.Context,
            contentHash,
            containerPath: "stash",
            request.CapturedUtc,
            request.DecodeRevision,
            provenance,
            reconstruction,
            totalContainerCells: new EvidencedValue<int?>(
                "stash.capture.total-cells",
                null,
                new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "stash.capture.total-cells.unresolved"),
                provenance),
            confirmsContainerStart: true);
        var assemblyRequest = new StashScanAssemblyRequest(
            $"stash-scan-{request.ArtifactId}",
            $"stash-snapshot-{request.ArtifactId}",
            request.SessionId,
            scope,
            profile.Context.DataSnapshot.SnapshotId,
            assembledUtc,
            [frame]);
        await _workflow.CompleteAsync(assemblyRequest, Guid.NewGuid(), makeCurrent: true, cancellationToken).ConfigureAwait(false);
    }
}
