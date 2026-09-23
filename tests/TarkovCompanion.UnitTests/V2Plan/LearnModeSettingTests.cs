using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.V2Plan;

public sealed class LearnModeSettingTests
{
    [Fact]
    public void Learn_mode_is_off_until_the_player_turns_it_on_and_the_choice_is_remembered()
    {
        var store = new MemoryLayoutStore();
        var first = new LearnModeSetting(store);

        Assert.False(first.IsEnabled);

        first.IsEnabled = true;

        Assert.Equal("on", store.Get(WorkspaceLayoutKeys.PlanLearnMode));
        Assert.True(new LearnModeSetting(store).IsEnabled);
    }

    [Fact]
    public void Plan_rows_reuse_the_reason_already_computed_for_their_kind()
    {
        var keep = new KeepListRowViewModel(
            "bolts",
            "Bolts",
            "B",
            false,
            ["2 for Gunsmith 4", "1 for Workbench 2"]);
        var hideout = new HideoutRequirementRowViewModel("Bolts", "2", "0", "2", false)
        {
            LearnReason = "Keep: needed for Workbench level 2",
        };
        var upgrade = new HideoutUpgradeStepRowViewModel(
            "1",
            "Workbench · level 2",
            "1 short",
            "Mechanic loyalty 2",
            false);

        Assert.Equal("Keep: 2 for Gunsmith 4; 1 for Workbench 2", keep.LearnReason);
        Assert.Equal("Keep: needed for Workbench level 2", hideout.LearnReason);
        Assert.Equal("Gate: Mechanic loyalty 2", upgrade.LearnReason);
    }

    [Fact]
    public void A_barter_rows_learn_line_uses_its_priced_readiness_result()
    {
        var row = new IntelTradeRowViewModel(
            "barter",
            "Barter",
            [],
            new("out", "Toolset", 1),
            "Mechanic",
            "Loyalty 2",
            string.Empty,
            "₽12,000 profit",
            false,
            false,
            IntelTradeReadiness.Ready,
            null!);

        Assert.Equal("Ready: ₽12,000 profit", row.LearnReason);
    }

    private sealed class MemoryLayoutStore : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
