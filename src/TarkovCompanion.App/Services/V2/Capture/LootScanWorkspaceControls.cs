using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Writes the player's Loot Scan choices where the scan reads them, then decides the scan again.
/// </summary>
/// <remarks>
/// A pin, a wishlist entry and an item rule go into the active profile through the same
/// compare-and-swap every other profile write uses. The phase and risk go into the session's
/// preference. Either way the frame already on screen is evaluated again from its retained,
/// pixel-free reading, so the change shows at once and no second screenshot is needed.
/// </remarks>
public sealed class LootScanWorkspaceControls(
    IProfileRuntimeContextService profiles,
    LootScanRaidPreference preference,
    LootScanCaptureHandoff handoff,
    ILogger<LootScanWorkspaceControls>? logger = null) : ILootScanWorkspaceControls
{
    private readonly IProfileRuntimeContextService _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly LootScanRaidPreference _preference = preference ?? throw new ArgumentNullException(nameof(preference));
    private readonly LootScanCaptureHandoff _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
    private readonly ILogger<LootScanWorkspaceControls> _logger = logger ?? NullLogger<LootScanWorkspaceControls>.Instance;

    public RecommendationRaidRisk Risk => _preference.Risk;

    public RecommendationRaidPhase? Phase => _preference.Phase;

    public bool IsPinned(string itemId) =>
        _profiles.Current.ActiveProfile is { } profile && LootScanProfileRules.IsPinned(profile.Progress, itemId);

    public bool IsWishlisted(string itemId) =>
        _profiles.Current.ActiveProfile is { } profile && LootScanProfileRules.IsWishlisted(profile.Progress, itemId);

    public LootScanItemRule RuleFor(string itemId) =>
        _profiles.Current.ActiveProfile is { } profile
            ? LootScanProfileRules.RuleFor(profile.Progress, itemId)
            : LootScanItemRule.None;

    public Task SetRiskAsync(RecommendationRaidRisk risk)
    {
        _preference.Risk = risk;
        return _handoff.ReevaluateLastAsync(CancellationToken.None);
    }

    public Task SetPhaseAsync(RecommendationRaidPhase? phase)
    {
        _preference.Phase = phase;
        return _handoff.ReevaluateLastAsync(CancellationToken.None);
    }

    public Task SetPinnedAsync(string itemId, bool pinned) =>
        WriteAsync(progress => LootScanProfileRules.WithPin(progress, itemId, pinned));

    public Task SetWishlistedAsync(string itemId, bool wishlisted) =>
        WriteAsync(progress => LootScanProfileRules.WithWishlist(progress, itemId, wishlisted));

    public Task SetRuleAsync(string itemId, LootScanItemRule rule) =>
        WriteAsync(progress => LootScanProfileRules.WithRule(
            progress,
            itemId,
            rule switch
            {
                LootScanItemRule.AlwaysTake => LootScanProfileRules.TakeRule,
                LootScanItemRule.AlwaysLeave => LootScanProfileRules.LeaveRule,
                _ => null,
            }));

    private async Task WriteAsync(Func<ProfileProgress, ProfileProgress> change)
    {
        var expected = _profiles.Current;
        if (expected.ActiveProfile is not { } profile)
        {
            return;
        }

        try
        {
            await _profiles.UpdateActiveProgressAsync(expected, change(profile.Progress), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The profile moved under the write, or could not be saved. The scan on screen still
            // reflects what is stored, so it is left as it is rather than showing a change that
            // did not happen.
            _logger.LogWarning(exception, "Could not save a Loot Scan item choice to the profile.");
            return;
        }

        await _handoff.ReevaluateLastAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
