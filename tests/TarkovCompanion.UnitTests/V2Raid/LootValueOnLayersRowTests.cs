using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#922] The minimum value and its basis are chosen on the Layers menu's loot row as well as on
/// the loot card, and there is one state behind both.
/// </summary>
public sealed class LootValueOnLayersRowTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    [Fact]
    public void A_value_chosen_on_the_layers_row_is_the_value_the_card_shows_and_the_one_remembered()
    {
        var layout = new MemoryLayout();
        var setting = new LootValueFilterSetting(layout);
        HighValueLootLayerViewModel? loot = null;
        loot = new HighValueLootLayerViewModel(
            Result(),
            setting.Apply(HighValueLootLayerFilterState.Default),
            null,
            null,
            true,
            Presentation,
            // The host's round trip: remember the filter and present it back, as the Raid page does.
            state =>
            {
                setting.Set(state.Filter);
                loot!.Present(Result(), state, null, null);
            },
            _ => { });
        var groups = MapLayerGroups.Group([], Presentation, loot);
        Assert.Empty(groups);

        // The row and the card both bind this one view model: a press on either goes through it.
        loot.ValueThresholdChoices.Single(choice => choice.Id == "threshold-250000").SelectCommand.Execute(null);

        Assert.Equal("threshold-250000", loot.ValueThresholdChoices.Single(choice => choice.IsSelected).Id);
        Assert.StartsWith("250k", loot.CompactFilterLabel, StringComparison.Ordinal);
        Assert.Equal("250000", layout.Get(WorkspaceLayoutKeys.RaidLootValueThreshold));

        // A cockpit opened later reads the same value back.
        var restored = new LootValueFilterSetting(layout).Apply(HighValueLootLayerFilterState.Default);
        Assert.Equal(250_000, restored.Filter.EffectiveMinimumValueRoubles);
    }

    private static HighValueLootLayerResult Result() => new(
        HighValueLootLayerService.Layer,
        "customs",
        "transform-1",
        HighValueLootFilter.Default,
        new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
        "Potential spawns · Data unavailable",
        null,
        null,
        [],
        [],
        []);

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
