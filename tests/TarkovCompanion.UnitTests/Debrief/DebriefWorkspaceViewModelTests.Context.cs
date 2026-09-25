using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.Profiles;

namespace TarkovCompanion.UnitTests.Debrief;

/// <summary>[#269] Debrief lists and counts the active profile's mode and wipe unless asked for everything.</summary>
public sealed partial class DebriefWorkspaceViewModelTests
{
    private sealed class FixedRaidContext(RaidContextView view) : IRaidContextSource
    {
        public RaidContextView Current() => view;
    }

    [Fact]
    public async Task Other_modes_wipes_and_profiles_are_out_of_the_list_and_totals_until_show_all()
    {
        var changed = Started.AddDays(-7);
        var original = ProfileV2Fixtures.Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3");
        var active = new ProfileRecord(
            new ProfileContext(original.Context.Identity, ProfileGameMode.Pvp, new WipeSeason("Wipe 4"), original.Context.Locale, original.Context.DataSnapshot),
            original.Name, original.Progress, original.Lifecycle, changed,
            ProfileWipeHistory.Record(original.ExtensionJson, "Wipe 3", "Wipe 4", changed));
        var other = ProfileV2Fixtures.Profile(2, "h", ProfileGameMode.Pvp, "x", wipe: "Wipe 4");
        var id = active.Context.Identity.ProfileId;
        var service = new FakeRaidHistoryService();
        RaidHistoryEntry Raid(Guid profile, string map, string mode, DateTimeOffset start) =>
            new(Guid.NewGuid(), profile, map, mode, start, start.AddMinutes(20), "Survived", null);
        service.Seed(Raid(id, "customs", "Regular", Started));
        service.Seed(Raid(id, "woods", "Regular", Started.AddHours(-1)));
        service.Seed(Raid(id, "shoreline", "Pve", Started.AddHours(-2)));
        service.Seed(Raid(id, "factory", "Regular", changed.AddDays(-1)));
        service.Seed(Raid(other.Context.Identity.ProfileId, "reserve", "Regular", Started.AddHours(-3)));
        var viewModel = new DebriefWorkspaceViewModel(
            service,
            TestPaths(),
            raidContext: new FixedRaidContext(new RaidContextView(active, [active, other])));

        await viewModel.LoadAsync();

        Assert.Equal(["customs", "woods"], viewModel.Raids.Select(row => row.MapLabel).OrderBy(map => map).ToArray());
        Assert.All(viewModel.Raids, row => Assert.Equal("Wipe 4", row.WipeLabel));
        Assert.Equal("2 raids · PvP · Wipe 4", viewModel.Status);
        Assert.Equal(3, viewModel.OtherContextCount);
        Assert.Equal("Show all (3)", viewModel.ContextToggleLabel);
        Assert.Equal(["customs", "woods"], viewModel.MapStats.Select(map => map.MapLabel).OrderBy(map => map).ToArray());

        viewModel.ShowAllContexts = true;

        Assert.Equal(5, viewModel.Raids.Count);
        Assert.Equal("5 raids · all modes and wipes", viewModel.Status);
        Assert.Equal("Wipe 3", viewModel.Raids.Single(row => row.MapLabel == "factory").WipeLabel);
        Assert.Equal("This profile only", viewModel.ContextToggleLabel);
    }

    [Fact]
    public async Task An_empty_context_names_itself_and_what_it_hides_and_offers_show_all()
    {
        var active = ProfileV2Fixtures.Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 4");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(Guid.NewGuid(), active.Context.Identity.ProfileId, "customs", "Pve", Started, Started.AddMinutes(20), "Survived", null));
        service.Seed(new RaidHistoryEntry(Guid.NewGuid(), active.Context.Identity.ProfileId, "woods", "Pve", Started.AddHours(-1), Started.AddMinutes(-40), "Survived", null));
        var viewModel = new DebriefWorkspaceViewModel(
            service,
            TestPaths(),
            raidContext: new FixedRaidContext(new RaidContextView(active, [active])));

        await viewModel.LoadAsync();

        Assert.True(viewModel.ShowsNoRaids);
        Assert.Equal("0 raids · PvP · Wipe 4", viewModel.Status);
        Assert.Equal("No raids in PvP · Wipe 4 yet. 2 raids from other modes or wipes are hidden.", viewModel.NoRaidsMessage);
        Assert.True(viewModel.ShowsEmptyReset);
        Assert.Equal("Show all (2)", viewModel.EmptyResetLabel);

        viewModel.ShowEverythingCommand.Execute(null);

        Assert.Equal(2, viewModel.Raids.Count);
    }

    [Fact]
    public async Task Without_a_profile_context_every_raid_is_listed_as_before()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pve", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.Single(viewModel.Raids);
        Assert.False(viewModel.HasOtherContexts);
        Assert.Equal("1 raid", viewModel.Status);
    }
}
