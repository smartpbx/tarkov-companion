using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// The single <see cref="ICaptureResultHandoff"/> #271's coordinator resolves, routing by intent to
/// whichever workspace-specific handoff owns it.
/// </summary>
/// <remarks>
/// [V2 rough package 60 — Intel scan] #287. Every intent but Loot and Stash used to fall into a
/// default that returned Accepted and produced nothing, so a player who pointed Capture at an item
/// screen got a progress bar and then silence. Those intents now reach
/// <see cref="IntelCaptureHandoff"/>, which publishes whichever catalog item the frame was read
/// as. Extracts, map and health screens still fall through: nothing reads those yet, and
/// acknowledging is more honest than inventing an answer.
/// </remarks>
public sealed class CompositeCaptureResultHandoff(
    LootScanCaptureHandoff lootScan,
    StashScanCaptureHandoff stashScan,
    IntelCaptureHandoff intel) : ICaptureResultHandoff
{
    private readonly LootScanCaptureHandoff _lootScan = lootScan ?? throw new ArgumentNullException(nameof(lootScan));
    private readonly StashScanCaptureHandoff _stashScan = stashScan ?? throw new ArgumentNullException(nameof(stashScan));
    private readonly IntelCaptureHandoff _intel = intel ?? throw new ArgumentNullException(nameof(intel));

    public ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.EffectiveIntent switch
        {
            // One named item and no lattice is an inspect screen, not a container.
            ScanIntent.Loot when request.Analysis.Grid is null && request.Analysis.Identified.Count > 0 =>
                _intel.AcceptItemAsync(request, cancellationToken),
            ScanIntent.Loot => _lootScan.AcceptAsync(request, cancellationToken),
            ScanIntent.Stash => _stashScan.AcceptAsync(request, cancellationToken),
            var intent when IntelCaptureHandoff.Intents.Contains(intent) =>
                _intel.AcceptAsync(request, cancellationToken),
            _ => ValueTask.FromResult(CaptureHandoffResult.Accepted),
        };
    }
}
