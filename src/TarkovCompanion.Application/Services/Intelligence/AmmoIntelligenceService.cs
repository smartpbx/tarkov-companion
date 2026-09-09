using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Intelligence;

public sealed record AmmoAvailability(
    string AmmoItemId,
    int MinimumPlayerLevel,
    IReadOnlyDictionary<string, int> RequiredTraderLevels,
    string? RequiredTaskId,
    IReadOnlySet<GameMode> GameModes);

public sealed class AmmoIntelligenceService : IAmmoIntelligenceService
{
    private readonly IReadOnlyDictionary<string, AmmoStats> _stats;
    private readonly IReadOnlyDictionary<string, AmmoPackContents> _packs;
    private readonly IReadOnlyDictionary<string, AmmoAvailability> _availability;

    public AmmoIntelligenceService(
        IEnumerable<AmmoStats> stats,
        IEnumerable<AmmoPackContents>? packs = null,
        IEnumerable<AmmoAvailability>? availability = null)
    {
        ArgumentNullException.ThrowIfNull(stats);

        _stats = stats.ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        _packs = (packs ?? []).ToDictionary(x => x.PackItemId, StringComparer.Ordinal);
        _availability = (availability ?? []).ToDictionary(x => x.AmmoItemId, StringComparer.Ordinal);
    }

    public Task<AmmoIntelligence?> GetAsync(
        string itemId,
        PlayerProfile? profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedItemId = _packs.GetValueOrDefault(itemId)?.AmmoItemId ?? itemId;
        var stat = _stats.GetValueOrDefault(resolvedItemId);
        return Task.FromResult(stat is null ? null : BuildIntelligence(stat, profile));
    }

    public Task<IReadOnlyList<AmmoIntelligence>> GetCaliberAsync(
        string caliber,
        PlayerProfile? profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caliber);
        cancellationToken.ThrowIfCancellationRequested();

        var intelligence = RankedCaliber(caliber)
            .Select(x => BuildIntelligence(x, profile))
            .Where(x => profile is null || x.ObtainableForProfile)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AmmoIntelligence>>(intelligence);
    }

    public AmmoPackContents? ResolvePack(string packItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packItemId);
        return _packs.GetValueOrDefault(packItemId);
    }

    private AmmoIntelligence BuildIntelligence(AmmoStats stat, PlayerProfile? profile)
    {
        var caliber = RankedCaliber(stat.Caliber);
        var index = caliber.FindIndex(x => StringComparer.Ordinal.Equals(x.ItemId, stat.ItemId));
        var rank = index + 1;
        var tier = TierForRank(rank, caliber.Count);
        var ratings = Enumerable.Range(1, 6).ToDictionary(x => x, x => RateArmor(stat.Penetration, x));
        var obtainable = IsObtainable(stat.ItemId, profile);
        var sourceConfidence = stat.Provenance.Confidence?.Value ?? 0.80;
        var confidence = new Confidence(Math.Min(sourceConfidence, 0.80));
        var tracerText = stat.IsTracer ? " tracer" : string.Empty;

        return new(
            stat,
            tier,
            ratings,
            $"Ranks {rank} of {caliber.Count} for {stat.Caliber} by penetration, then damage.{tracerText}",
            $"Heuristic, not a live detection: {stat.Penetration} penetration is compared with armor class x 10; damage is {stat.Damage}.",
            obtainable,
            confidence);
    }

    private List<AmmoStats> RankedCaliber(string caliber) => _stats.Values
        .Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.Caliber, caliber))
        .OrderByDescending(x => x.Penetration)
        .ThenByDescending(x => x.Damage)
        .ThenBy(x => x.ItemId, StringComparer.Ordinal)
        .ToList();

    private bool IsObtainable(string itemId, PlayerProfile? profile)
    {
        if (profile is null || !_availability.TryGetValue(itemId, out var rule))
        {
            return true;
        }

        return profile.Level >= rule.MinimumPlayerLevel &&
               (rule.GameModes.Count == 0 || rule.GameModes.Contains(profile.GameMode)) &&
               (rule.RequiredTaskId is null || profile.CompletedTaskIds.Contains(rule.RequiredTaskId)) &&
               rule.RequiredTraderLevels.All(x => profile.TraderLevels.GetValueOrDefault(x.Key) >= x.Value);
    }

    private static ArmorEffectiveness RateArmor(int penetration, int armorClass)
    {
        var margin = penetration - (armorClass * 10);
        return margin switch
        {
            >= 10 => ArmorEffectiveness.Excellent,
            >= 0 => ArmorEffectiveness.Good,
            >= -5 => ArmorEffectiveness.Fair,
            >= -10 => ArmorEffectiveness.Limited,
            _ => ArmorEffectiveness.Poor,
        };
    }

    private static string TierForRank(int rank, int count)
    {
        if (rank <= 0 || count <= 0)
        {
            return "Unknown";
        }

        if (rank == 1)
        {
            return "S";
        }

        var percentile = (double)(rank - 1) / Math.Max(1, count - 1);
        return percentile switch
        {
            <= 0.25 => "A",
            <= 0.50 => "B",
            <= 0.75 => "C",
            _ => "D",
        };
    }
}
