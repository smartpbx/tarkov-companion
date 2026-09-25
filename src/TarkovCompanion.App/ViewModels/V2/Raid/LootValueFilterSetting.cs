using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.LootSpawns;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>The remembered minimum and value basis for potential loot on the raid map.</summary>
/// <remarks>
/// This is a player's display preference, not raid intelligence. A hand-edited layout can contain
/// arbitrary text, so only the five thresholds the UI offers and defined bases are restored.
/// </remarks>
internal sealed class LootValueFilterSetting
{
    public const long DefaultThreshold = 50_000;
    private static readonly IReadOnlySet<long> SupportedThresholds =
        new HashSet<long> { 0, 50_000, 100_000, 250_000, 500_000 };

    private readonly IWorkspaceLayoutStore? _store;

    public LootValueFilterSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Reload();
    }

    /// <summary>[#902] Reads the stored threshold and basis again, after Backup &amp; reset replaced them.</summary>
    public void Reload()
    {
        Threshold = ParseThreshold(_store?.Get(WorkspaceLayoutKeys.RaidLootValueThreshold));
        Basis = ParseBasis(_store?.Get(WorkspaceLayoutKeys.RaidLootValueBasis));
    }

    public long Threshold { get; private set; }

    public LootSpawnValueBasis Basis { get; private set; }

    /// <remarks>
    /// [#902 P4] The floor is always cleared: the loot layer follows the map's own floor. A loot
    /// floor chosen on one map was carried to the next, where no spawn is on it, and emptied the
    /// layer with no chip lit to say why.
    /// </remarks>
    public HighValueLootLayerFilterState Apply(HighValueLootLayerFilterState state) =>
        state.WithFilter(Clone(state.Filter, Basis, Threshold));

    public void Set(HighValueLootFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Threshold = SupportedThresholds.Contains(filter.EffectiveMinimumValueRoubles)
            ? filter.EffectiveMinimumValueRoubles
            : DefaultThreshold;
        Basis = Enum.IsDefined(filter.ValueBasis) ? filter.ValueBasis : LootSpawnValueBasis.BestNet;
        _store?.Set(WorkspaceLayoutKeys.RaidLootValueThreshold, Threshold.ToString(CultureInfo.InvariantCulture));
        _store?.Set(WorkspaceLayoutKeys.RaidLootValueBasis, Basis.ToString());
    }

    internal static long ParseThreshold(string? stored) =>
        long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) &&
        SupportedThresholds.Contains(threshold)
            ? threshold
            : DefaultThreshold;

    internal static LootSpawnValueBasis ParseBasis(string? stored) =>
        Enum.TryParse<LootSpawnValueBasis>(stored, ignoreCase: true, out var basis) && Enum.IsDefined(basis)
            ? basis
            : LootSpawnValueBasis.BestNet;

    private static HighValueLootFilter Clone(
        HighValueLootFilter filter,
        LootSpawnValueBasis valueBasis,
        long minimumValueRoubles) => new(
        valueBasis,
        filter.Thresholds,
        filter.MaximumPriceAge,
        filter.MaximumSourceAge,
        filter.MinimumConfidence,
        filter.IncludeProfileRelevant,
        null,
        filter.ItemIds,
        filter.Categories,
        minimumValueRoubles);
}
