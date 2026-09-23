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
/// loot lattice (<see cref="InventoryGridSurface.VisibleLoot"/>) on <see cref="CaptureAnalysis.Grid"/>,
/// and the backpack from the same frame on <see cref="CaptureAnalysis.CarriedGrid"/>. A frame
/// with no backpack read reconstructs an empty carried grid, which the decision service reports
/// as unavailable rather than this adapter fabricating recognized items.
/// </remarks>
public sealed class LootScanCaptureHandoff(
    IProfileRuntimeContextService profileContext,
    InventoryGridReconstructor gridReconstructor,
    LootScanDecisionService decisionService,
    TimeProvider? timeProvider = null,
    ILogger<LootScanCaptureHandoff>? logger = null,
    LootScanRecommendationSource? recommendations = null,
    IObservedInventoryEvidenceReader? observedInventory = null,
    // #572: profile lookup, recommendation building and (in AcceptAsync only, never on a
    // re-decide) the scan's total are marked here, then Complete()'d - see ICaptureStageTimeline.
    ICaptureStageTimeline? stageTimeline = null) : ICaptureResultHandoff
{
    private readonly LootScanRecommendationSource? _recommendations = recommendations;
    private readonly IObservedInventoryEvidenceReader? _observedInventory = observedInventory;
    private readonly ICaptureStageTimeline? _stageTimeline = stageTimeline;
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
    /// #572: raised the moment a Loot-intent capture is accepted, before grid reconstruction,
    /// profile lookup or recommendation - which measured 350 ms to well over a second on real and
    /// composed frames respectively. The Loot page must show something long before that finishes;
    /// this is what lets the shell navigate there immediately instead of only once the full
    /// result is ready. Never raised from <see cref="ReevaluateLastAsync"/>, which redecides an
    /// already-shown result and has nothing new to announce.
    /// </summary>
    public event EventHandler? LootScanStarted;

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

        LootScanStarted?.Invoke(this, EventArgs.Empty);
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
                        CarriedGrids = request.Analysis.CarriedGrids,
                        HasCompleteCarriedCoverage = request.Analysis.HasCompleteCarriedCoverage,
                    },
                    profile,
                    cancellationToken)
                .ConfigureAwait(false);
            LootScanEvaluated?.Invoke(this, result);
            // #572: after the event above, which is what carries the result to
            // V2ShellCaptureBridge.ShowLootScanResult - the total below therefore covers first
            // paint's dispatch too, not only evaluation. Only here, never from ReevaluateLastAsync:
            // re-deciding the same frame for a pin or a phase change is not a scan's latency.
            _stageTimeline?.Complete(request.CorrelationId, _timeProvider.GetUtcNow());
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
        var reconstructStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var visibleLoot = _gridReconstructor.Reconstruct(
            request.Grid is { Surface: InventoryGridSurface.VisibleLoot } visibleLootRequest
                ? visibleLootRequest
                : new(InventoryGridSurface.VisibleLoot, lattice: null, occupiedCells: []),
            cancellationToken);
        var carriedRequests = request.CarriedGrids.Count > 0
            ? request.CarriedGrids
            : request.CarriedGrid is { Surface: InventoryGridSurface.CarriedInventory } carriedRequest
                ? [new(CarriedGridIdentity.PrimaryBackpack, carriedRequest)]
                : [];
        var carriedGrids = carriedRequests
            .Select(carried => new CarriedGridReconstructionResult(
                carried.Identity,
                _gridReconstructor.Reconstruct(carried.Reconstruction, cancellationToken)))
            .ToArray();
        var carriedInventory = carriedGrids
            .FirstOrDefault(carried => carried.Identity == CarriedGridIdentity.PrimaryBackpack)
            ?.Reconstruction
            ?? _gridReconstructor.Reconstruct(
                new(InventoryGridSurface.CarriedInventory, lattice: null, occupiedCells: []),
                cancellationToken);
        _stageTimeline?.Mark(request.CorrelationId, "grid_reconstruct", reconstructStopwatch.Elapsed);
        // The pipeline's content hash is the only thing that survives the pixel-free handoff
        // boundary; reusing it for both sides keeps this frame "current" without a redecode.
        var contentHash = request.ContentSha256;
        var evaluatedUtc = _timeProvider.GetUtcNow();
        var holdingsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var holdings = await ReadHoldingsAsync(scope, profile.Context.DataSnapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
        _stageTimeline?.Mark(request.CorrelationId, "profile_lookup", holdingsStopwatch.Elapsed);
        var recommendStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var recommendationContext = new LootScanRecommendationContext(
            scope,
            profile.Context.DataSnapshot.SnapshotId,
            holdings,
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
        var carriedPolicies = new List<LootScanCarriedPolicy>();
        if (_recommendations is not null)
        {
            foreach (var carried in carriedGrids)
            {
                carriedPolicies.AddRange(await _recommendations.BuildCarriedPoliciesAsync(
                        carried.Reconstruction,
                        request.SessionId,
                        request.ArtifactId,
                        request.DecodeRevision,
                        contentHash,
                        evaluatedUtc,
                        profile,
                        cancellationToken,
                        carried.Identity)
                    .ConfigureAwait(false));
            }
        }
        _stageTimeline?.Mark(request.CorrelationId, "recommendation", recommendStopwatch.Elapsed);
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
            carriedGrids,
            request.HasCompleteCarriedCoverage,
            candidates,
            carriedPolicies);
        var decideStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var decided = _decisionService.Evaluate(lootScanRequest, cancellationToken);
        _stageTimeline?.Mark(request.CorrelationId, "decide", decideStopwatch.Elapsed);
        return decided;
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
    /// The player's own backpack as the same frame showed it, where the in-raid Gear screen
    /// showed one. Without it a scan says the carried grid is unread and offers no fit.
    /// </summary>
    public GridReconstructionRequest? CarriedGrid { get; init; }

    /// <summary>Every separately framed backpack, rig and pocket grid read from the frame.</summary>
    public IReadOnlyList<CarriedGridReconstructionRequest> CarriedGrids { get; init; } = [];

    /// <summary>Whether the frame supports a no-fit claim across carried space, not only a fit.</summary>
    public bool HasCompleteCarriedCoverage { get; init; } = true;
}
