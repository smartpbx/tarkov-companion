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
    IntelCaptureHandoff intel,
    FleaCaptureHandoff? flea = null) : ICaptureResultHandoff
{
    private readonly LootScanCaptureHandoff _lootScan = lootScan ?? throw new ArgumentNullException(nameof(lootScan));
    private readonly StashScanCaptureHandoff _stashScan = stashScan ?? throw new ArgumentNullException(nameof(stashScan));
    private readonly IntelCaptureHandoff _intel = intel ?? throw new ArgumentNullException(nameof(intel));

    /// <summary>
    /// Raised for every accepted capture that was not read as a loot container (#572): the player
    /// is looking at something else, which is the shell's cue that they moved on from Loot. Never
    /// raised for the true Loot-grid branch, and never for a capture that could not be resolved at
    /// all (an ambiguous or unknown screen never reaches <see cref="AcceptAsync"/>).
    /// </summary>
    public event EventHandler<ScanIntent>? NonLootIntentHandled;

    public ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // One named item and no lattice is an inspect screen, not a container.
        var isLootGrid = request.EffectiveIntent == ScanIntent.Loot &&
            (request.Analysis.Grid is not null || request.Analysis.Identified.Count == 0);
        if (!isLootGrid)
        {
            NonLootIntentHandled?.Invoke(this, request.EffectiveIntent);
        }

        return request.EffectiveIntent switch
        {
            ScanIntent.Loot when !isLootGrid => _intel.AcceptItemAsync(request, cancellationToken),
            ScanIntent.Loot => _lootScan.AcceptAsync(request, cancellationToken),
            ScanIntent.Stash => _stashScan.AcceptAsync(request, cancellationToken),
            // #283: an open case under an Ammo or Keys case scan is one of its screenshots; the
            // stash handoff ignores it when no case scan is collecting, and Intel still names it.
            ScanIntent.Ammo or ScanIntent.Keys => CaseThenIntelAsync(request, cancellationToken),
            // [f920 capture] #284: legible flea rows go to Intel > Flea. A flea capture with no
            // rows still names its item where it can, as it did before.
            ScanIntent.Flea when flea is not null && request.Analysis.FleaListings.Count > 0 =>
                flea.AcceptAsync(request, cancellationToken),
            var intent when IntelCaptureHandoff.Intents.Contains(intent) =>
                _intel.AcceptAsync(request, cancellationToken),
            _ => ValueTask.FromResult(CaptureHandoffResult.Accepted),
        };
    }

    private async ValueTask<CaptureHandoffResult> CaseThenIntelAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        await _stashScan.AcceptAsync(request, cancellationToken).ConfigureAwait(false);
        return await _intel.AcceptAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
