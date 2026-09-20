using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
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
    ILogger<LootScanCaptureHandoff>? logger = null,
    LootScanRecommendationSource? recommendations = null,
    IObservedInventoryEvidenceReader? observedInventory = null) : ICaptureResultHandoff
{
    private readonly LootScanRecommendationSource? _recommendations = recommendations;
    private readonly IObservedInventoryEvidenceReader? _observedInventory = observedInventory;
    private readonly IProfileRuntimeContextService _profileContext =
        profileContext ?? throw new ArgumentNullException(nameof(profileContext));
    private readonly InventoryGridReconstructor _gridReconstructor =
        gridReconstructor ?? throw new ArgumentNullException(nameof(gridReconstructor));
    private readonly LootScanDecisionService _decisionService =
        decisionService ?? throw new ArgumentNullException(nameof(decisionService));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<LootScanCaptureHandoff> _logger = logger ?? NullLogger<LootScanCaptureHandoff>.Instance;

    private readonly Lock _lastGate = new();
    private LootScanFrame? _lastFrame;

    /// <summary>Raised after a Loot-intent capture is evaluated. Never raised for other intents.</summary>
    public event EventHandler<LootScanResult>? LootScanEvaluated;

    /// <summary>
    /// Decides the last scanned frame again, against the profile and raid context as they are now.
    /// </summary>
    /// <remarks>
    /// Pinning an item or changing the raid phase changes the answer and not the picture. The
    /// frame's reading is pixel-free and already held, so it is evaluated again as it stands.
    /// Nothing happens where no frame has been scanned or no profile is active.
    /// </remarks>
    public async Task<bool> ReevaluateLastAsync(CancellationToken cancellationToken)
    {
        LootScanFrame? frame;
        lock (_lastGate)
        {
            frame = _lastFrame;
        }

        if (frame is null || _profileContext.Current.ActiveProfile is not { } profile)
        {
            return false;
        }

        try
        {
            var result = await EvaluateAsync(frame, profile, cancellationToken).ConfigureAwait(false);
            LootScanEvaluated?.Invoke(this, result);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not decide the last loot scan again.");
            return false;
        }
    }

    public async ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EffectiveIntent != ScanIntent.Loot)
        {
            return CaptureHandoffResult.Accepted;
        }

        var snapshot = _profileContext.Current;
        if (snapshot.ActiveProfile is not { } profile)
        {
            // Additive: a capture with nowhere to bind its profile scope is acknowledged, not
            // rejected. The player still needs to pick or create a profile; that is Setup's job.
            _logger.LogInformation("Skipped a loot scan because no active profile is selected yet.");
            return CaptureHandoffResult.Accepted;
        }

        try
        {
            var result = await EvaluateAsync(
                    new(
                        request.CorrelationId.ToString(),
                        request.SessionId,
                        request.CorrelationId,
                        request.Context,
                        request.ArtifactId,
                        request.DecodeRevision,
                        request.Analysis.ResultId,
                        request.Context.InitiatingDevice ?? "desktop",
                        request.Analysis.Grid)
                    {
                        CarriedGrid = request.Analysis.CarriedGrid,
                    },
                    profile,
                    cancellationToken)
                .ConfigureAwait(false);
            LootScanEvaluated?.Invoke(this, result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not evaluate a loot scan for capture session {SessionId}.", request.SessionId);
        }

        return CaptureHandoffResult.Accepted;
    }

    /// <summary>
    /// Evaluates one analysed frame. Public so the render preview can show the workspace over a
    /// genuinely scanned frame without standing up a capture session.
    /// </summary>
    public async Task<LootScanResult> EvaluateAsync(LootScanFrame request, ProfileRecord profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        lock (_lastGate)
        {
            _lastFrame = request;
        }

        var scope = new InventoryProfileScope(
            profile.Context.Identity.ProfileId,
            profile.Context.Identity.Generation,
            profile.Context.Mode.ToString());
        var visibleLoot = _gridReconstructor.Reconstruct(
            request.Grid is { Surface: InventoryGridSurface.VisibleLoot } visibleLootRequest
                ? visibleLootRequest
                : new(InventoryGridSurface.VisibleLoot, lattice: null, occupiedCells: []),
            cancellationToken);
        var carriedInventory = _gridReconstructor.Reconstruct(
            request.CarriedGrid is { Surface: InventoryGridSurface.CarriedInventory } carriedRequest
                ? carriedRequest
                : new(InventoryGridSurface.CarriedInventory, lattice: null, occupiedCells: []),
            cancellationToken);
        // The pipeline's content hash is the only thing that survives the pixel-free handoff
        // boundary; reusing it for both sides keeps this frame "current" without a redecode.
        var contentHash = request.ContentSha256;
        var evaluatedUtc = _timeProvider.GetUtcNow();
        var recommendationContext = new LootScanRecommendationContext(
            scope,
            profile.Context.DataSnapshot.SnapshotId,
            await ReadHoldingsAsync(scope, profile.Context.DataSnapshot.SnapshotId, cancellationToken).ConfigureAwait(false),
            raidContext: _recommendations is null
                ? null
                : await _recommendations.ReadRaidContextAsync(evaluatedUtc, cancellationToken).ConfigureAwait(false));
        IReadOnlyList<LootScanCandidateRecommendation> candidates = _recommendations is null
            ? []
            : await _recommendations.BuildAsync(
                    visibleLoot,
                    request.SessionId,
                    request.ArtifactId,
                    request.DecodeRevision,
                    contentHash,
                    scope,
                    profile.Context.DataSnapshot.SnapshotId,
                    evaluatedUtc,
                    cancellationToken,
                    profile)
                .ConfigureAwait(false);
        IReadOnlyList<LootScanCarriedPolicy> carriedPolicies = _recommendations is null
            ? []
            : await _recommendations.BuildCarriedPoliciesAsync(
                    carriedInventory,
                    request.SessionId,
                    request.ArtifactId,
                    request.DecodeRevision,
                    contentHash,
                    evaluatedUtc,
                    profile,
                    cancellationToken)
                .ConfigureAwait(false);
        var lootScanRequest = new LootScanRequest(
            request.ScanId,
            request.SessionId,
            request.CorrelationId,
            request.Context,
            request.ArtifactId,
            request.DecodeRevision,
            contentHash,
            contentHash,
            request.InitiatingDevice,
            evaluatedUtc,
            recommendationContext,
            visibleLoot,
            carriedInventory,
            candidates,
            carriedPolicies);
        return _decisionService.Evaluate(lootScanRequest, cancellationToken);
    }

    /// <summary>
    /// What a stash scan last saw the player holding, where one exists for this profile.
    /// </summary>
    /// <remarks>
    /// The engine subtracts holdings before it says "you still need two", and says so plainly
    /// when it has nothing to subtract. A snapshot from another profile or another data sync is
    /// not this profile's holdings, so it is passed over rather than handed on to be refused.
    /// </remarks>
    private async Task<ObservedInventoryEvidenceSnapshot?> ReadHoldingsAsync(
        InventoryProfileScope scope,
        string dataSnapshotId,
        CancellationToken cancellationToken)
    {
        if (_observedInventory is null)
        {
            return null;
        }

        try
        {
            var holdings = await _observedInventory.ReadCurrentAsync(scope, cancellationToken).ConfigureAwait(false);
            return holdings is not null &&
                   holdings.Scope == scope &&
                   string.Equals(holdings.DataSnapshotId, dataSnapshotId, StringComparison.Ordinal)
                ? holdings
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read observed holdings for a loot scan.");
            return null;
        }
    }
}

/// <summary>One analysed frame, as far as a loot evaluation needs to know it.</summary>
public sealed record LootScanFrame(
    string ScanId,
    CaptureSessionId SessionId,
    CaptureCorrelationId CorrelationId,
    CaptureContextMetadata Context,
    string ArtifactId,
    int DecodeRevision,
    string ContentSha256,
    string InitiatingDevice,
    GridReconstructionRequest? Grid)
{
    /// <summary>
    /// The player's own backpack as the same frame showed it, where something read it. Nothing
    /// in the capture pipeline does yet, so a scan says the carried grid is unread and offers no
    /// fit; a frame that does carry one is planned against it.
    /// </summary>
    public GridReconstructionRequest? CarriedGrid { get; init; }
}
