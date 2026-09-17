using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// The single <see cref="ICaptureResultHandoff"/> #271's coordinator resolves, routing by intent to
/// whichever workspace-specific handoff owns it. Ammo, Keys, and Quest-items grids have no handoff
/// yet - those workspaces are other packages' rough pass - so a capture with one of those intents
/// is acknowledged without producing advice, exactly as it was before either handoff below existed.
/// </summary>
public sealed class CompositeCaptureResultHandoff(
    LootScanCaptureHandoff lootScan,
    StashScanCaptureHandoff stashScan) : ICaptureResultHandoff
{
    private readonly LootScanCaptureHandoff _lootScan = lootScan ?? throw new ArgumentNullException(nameof(lootScan));
    private readonly StashScanCaptureHandoff _stashScan = stashScan ?? throw new ArgumentNullException(nameof(stashScan));

    public ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.EffectiveIntent switch
        {
            ScanIntent.Loot => _lootScan.AcceptAsync(request, cancellationToken),
            ScanIntent.Stash => _stashScan.AcceptAsync(request, cancellationToken),
            _ => ValueTask.FromResult(CaptureHandoffResult.Accepted),
        };
    }
}
