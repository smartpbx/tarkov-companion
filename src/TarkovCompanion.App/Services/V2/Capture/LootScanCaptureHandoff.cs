using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// The reviewed-result consumer #271 left for the composition owner to connect. A capture handed
/// off with the Loot intent is evaluated by the real #274/#282 decision service and published for
/// the shell to show; every other intent is acknowledged without producing advice, since the
/// workspaces that own them (Stash scan, Ammo, Keys, Map/extracts) are other packages' rough pass.
/// </summary>
/// <remarks>
/// #273's pixel-to-grid recognition runs in <see cref="TarkovCompanion.App.Services.V2.Capture.CaptureRecognitionPipeline"/>,
/// the one place pixels are still available; its result rides pixel-free on
/// <see cref="CaptureAnalysis.Grid"/> as far as this handoff. It only ever measures the visible
/// loot lattice (<see cref="InventoryGridSurface.VisibleLoot"/>): splitting the Loot screen's
/// second panel into its own <see cref="InventoryGridSurface.CarriedInventory"/> lattice needs
/// region-of-interest detection this pass does not attempt, so that surface still reconstructs an
/// intentionally empty grid and the decision service reports it honestly as unavailable rather
/// than this adapter fabricating recognized items.
/// </remarks>
public sealed class LootScanCaptureHandoff(
    IProfileRuntimeContextService profileContext,
    InventoryGridReconstructor gridReconstructor,
    LootScanDecisionService decisionService,
    TimeProvider? timeProvider = null,
    ILogger<LootScanCaptureHandoff>? logger = null) : ICaptureResultHandoff
{
    private readonly IProfileRuntimeContextService _profileContext =
        profileContext ?? throw new ArgumentNullException(nameof(profileContext));
    private readonly InventoryGridReconstructor _gridReconstructor =
        gridReconstructor ?? throw new ArgumentNullException(nameof(gridReconstructor));
    private readonly LootScanDecisionService _decisionService =
        decisionService ?? throw new ArgumentNullException(nameof(decisionService));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<LootScanCaptureHandoff> _logger = logger ?? NullLogger<LootScanCaptureHandoff>.Instance;

    /// <summary>Raised after a Loot-intent capture is evaluated. Never raised for other intents.</summary>
    public event EventHandler<LootScanResult>? LootScanEvaluated;

    public ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EffectiveIntent != ScanIntent.Loot)
        {
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }

        var snapshot = _profileContext.Current;
        if (snapshot.ActiveProfile is not { } profile)
        {
            // Additive: a capture with nowhere to bind its profile scope is acknowledged, not
            // rejected. The player still needs to pick or create a profile; that is Setup's job.
            _logger.LogInformation("Skipped a loot scan because no active profile is selected yet.");
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }

        try
        {
            LootScanEvaluated?.Invoke(this, Evaluate(request, profile, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not evaluate a loot scan for capture session {SessionId}.", request.SessionId);
        }

        return ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    private LootScanResult Evaluate(CaptureHandoffRequest request, ProfileRecord profile, CancellationToken cancellationToken)
    {
        var scope = new InventoryProfileScope(
            profile.Context.Identity.ProfileId,
            profile.Context.Identity.Generation,
            profile.Context.Mode.ToString());
        var recommendationContext = new LootScanRecommendationContext(
            scope,
            profile.Context.DataSnapshot.SnapshotId,
            inventory: null,
            raidContext: null);
        var visibleLoot = _gridReconstructor.Reconstruct(
            request.Analysis.Grid is { Surface: InventoryGridSurface.VisibleLoot } visibleLootRequest
                ? visibleLootRequest
                : new(InventoryGridSurface.VisibleLoot, lattice: null, occupiedCells: []),
            cancellationToken);
        var carriedInventory = _gridReconstructor.Reconstruct(
            new(InventoryGridSurface.CarriedInventory, lattice: null, occupiedCells: []),
            cancellationToken);
        // The pipeline's content hash is the only thing that survives the pixel-free handoff
        // boundary; reusing it for both sides keeps this frame "current" without a redecode.
        var contentHash = request.Analysis.ResultId;
        var lootScanRequest = new LootScanRequest(
            request.CorrelationId.ToString(),
            request.SessionId,
            request.CorrelationId,
            request.Context,
            request.ArtifactId,
            request.DecodeRevision,
            contentHash,
            contentHash,
            request.Context.InitiatingDevice ?? "desktop",
            _timeProvider.GetUtcNow(),
            recommendationContext,
            visibleLoot,
            carriedInventory,
            recommendations: [],
            carriedPolicies: []);
        return _decisionService.Evaluate(lootScanRequest, cancellationToken);
    }
}
