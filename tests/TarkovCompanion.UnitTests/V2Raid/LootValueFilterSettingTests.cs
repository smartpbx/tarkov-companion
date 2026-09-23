using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.LootSpawns;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class LootValueFilterSettingTests
{
    [Fact]
    public void A_choice_is_persisted_and_applied_by_the_next_cockpit()
    {
        var layout = new MemoryLayout();
        var first = new LootValueFilterSetting(layout);
        var chosen = new HighValueLootFilter(
            LootSpawnValueBasis.ValuePerSquare,
            LootSpawnValueThresholds.Default,
            HighValueLootFilter.Default.MaximumPriceAge,
            HighValueLootFilter.Default.MaximumSourceAge,
            HighValueLootFilter.Default.MinimumConfidence,
            minimumValueRoubles: 250_000);

        first.Set(chosen);
        var restarted = new LootValueFilterSetting(layout);
        var restored = restarted.Apply(HighValueLootLayerFilterState.Default).Filter;

        Assert.Equal(250_000, restored.EffectiveMinimumValueRoubles);
        Assert.Equal(LootSpawnValueBasis.ValuePerSquare, restored.ValueBasis);
        Assert.Equal("250000", layout.Get(WorkspaceLayoutKeys.RaidLootValueThreshold));
        Assert.Equal("ValuePerSquare", layout.Get(WorkspaceLayoutKeys.RaidLootValueBasis));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("nope", "also-nope")]
    [InlineData("12345", "ProfileUtility2")]
    public void Missing_or_invalid_layout_values_use_the_player_facing_defaults(string? threshold, string? basis)
    {
        var layout = new MemoryLayout();
        if (threshold is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidLootValueThreshold, threshold);
        }

        if (basis is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidLootValueBasis, basis);
        }

        var restored = new LootValueFilterSetting(layout).Apply(HighValueLootLayerFilterState.Default).Filter;

        Assert.Equal(50_000, restored.EffectiveMinimumValueRoubles);
        Assert.Equal(LootSpawnValueBasis.BestNet, restored.ValueBasis);
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
