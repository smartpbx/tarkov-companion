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
/// as. Extracts, map and health screens reach <see cref="UnsupportedScreenHandoff"/>, which says
/// what the screen was and that nothing reads it yet (#287).
///
/// [#712 1-1] One handler per screen kind replaces the intent switch: the detector that placed a
/// frame names its handler, and an intent (Read as…, a guided scan) only where no detector did.
/// </remarks>
public sealed class CompositeCaptureResultHandoff(
    LootScanCaptureHandoff lootScan,
    StashScanCaptureHandoff stashScan,
    IntelCaptureHandoff intel,
    FleaCaptureHandoff? flea = null,
    // #287: extracts, map and character screens say "not supported yet" instead of nothing.
    UnsupportedScreenHandoff? unsupported = null,
    // #712 0-12: a Loot capture that is one named item counts as an Item in the timing table.
    ICaptureStageTimeline? stageTimeline = null) : ICaptureResultHandoff
{
    private readonly LootScanCaptureHandoff _lootScan = lootScan ?? throw new ArgumentNullException(nameof(lootScan));
    private readonly StashScanCaptureHandoff _stashScan = stashScan ?? throw new ArgumentNullException(nameof(stashScan));
    private readonly IntelCaptureHandoff _intel = intel ?? throw new ArgumentNullException(nameof(intel));
    private Dictionary<ScreenKind, Func<CaptureHandoffRequest, CancellationToken, ValueTask<CaptureHandoffResult>>>? _handlerTable;

    private Dictionary<ScreenKind, Func<CaptureHandoffRequest, CancellationToken, ValueTask<CaptureHandoffResult>>> _handlers =>
        _handlerTable ??= BuildHandlers();

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
        var screen = ScreenFor(request);
        if (screen != ScreenKind.Loot)
        {
            NonLootIntentHandled?.Invoke(this, request.EffectiveIntent);
        }

        if (screen == ScreenKind.Item && request.EffectiveIntent == ScanIntent.Loot)
        {
            stageTimeline?.Classify(request.CorrelationId, CaptureTimelineKinds.Item);
        }

        return _handlers.TryGetValue(screen, out var handler)
            ? handler(request, cancellationToken)
            : ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    /// <summary>
    /// [#712 1-1] The screen a handed-on capture is of: the detector's answer where one placed it,
    /// otherwise the one the intent names ("Read as…", a guided case scan).
    /// </summary>
    /// <remarks>
    /// One named item and no lattice is an inspect screen, not a container, whichever said Loot.
    /// Ammo, Keys and Quest items keep their own entries: an open case is a case scan's frame.
    /// </remarks>
    internal static ScreenKind ScreenFor(CaptureHandoffRequest request)
    {
        var routed = request.Decision == CaptureReviewAction.UseDetected
            && request.Analysis.Routing is { Outcome: ScreenRoutingOutcome.Routed, Kind: { } kind }
                ? kind
                : (ScreenKind?)null;
        var screen = routed ?? request.EffectiveIntent switch
        {
            ScanIntent.Loot => ScreenKind.Loot,
            ScanIntent.Stash => ScreenKind.Stash,
            ScanIntent.Flea => ScreenKind.Flea,
            ScanIntent.ExtractsAndMap => ScreenKind.ExtractList,
            ScanIntent.HealthAndCharacter => ScreenKind.Health,
            _ => ScreenKind.Item,
        };
        return screen == ScreenKind.Loot && request.Analysis.Grid is null && request.Analysis.Identified.Count > 0
            ? ScreenKind.Item
            : screen;
    }

    private Dictionary<ScreenKind, Func<CaptureHandoffRequest, CancellationToken, ValueTask<CaptureHandoffResult>>> BuildHandlers()
    {
        var handlers = new Dictionary<ScreenKind, Func<CaptureHandoffRequest, CancellationToken, ValueTask<CaptureHandoffResult>>>
        {
            [ScreenKind.Loot] = _lootScan.AcceptAsync,
            // A stash nobody asked to scan is one more frame of a guided scan, or nothing: a single
            // unarmed frame must not replace the stash the player last scanned (1-11 merges them).
            [ScreenKind.Stash] = (request, token) =>
                request.Analysis.Routing is { Outcome: ScreenRoutingOutcome.Routed }
                    ? _stashScan.AcceptSeenAsync(request, token)
                    : _stashScan.AcceptAsync(request, token),
            [ScreenKind.Item] = (request, token) => request.EffectiveIntent switch
            {
                ScanIntent.Loot => _intel.AcceptItemAsync(request, token),
                // #283: an open case under an Ammo or Keys case scan is one of its screenshots; the
                // stash handoff ignores it when no case scan is collecting, and Intel still names it.
                ScanIntent.Ammo or ScanIntent.Keys => CaseThenIntelAsync(request, token),
                var intent when IntelCaptureHandoff.Intents.Contains(intent) => _intel.AcceptAsync(request, token),
                _ => ValueTask.FromResult(CaptureHandoffResult.Accepted),
            },
            // [f920 capture] #284: legible flea rows go to Intel > Flea. A flea capture with no
            // rows still names its item where it can, as it did before.
            [ScreenKind.Flea] = (request, token) => flea is not null && request.Analysis.FleaListings.Count > 0
                ? flea.AcceptAsync(request, token)
                : _intel.AcceptAsync(request, token),
        };
        if (unsupported is not null)
        {
            handlers[ScreenKind.ExtractList] = unsupported.AcceptAsync;
            handlers[ScreenKind.Health] = unsupported.AcceptAsync;
        }

        return handlers;
    }

    private async ValueTask<CaptureHandoffResult> CaseThenIntelAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        await _stashScan.AcceptAsync(request, cancellationToken).ConfigureAwait(false);
        return await _intel.AcceptAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
