using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Core.Domain.Ammo;
using static TarkovCompanion.UnitTests.V2Intel.IntelWorkspaceFakes;

namespace TarkovCompanion.UnitTests.V2Intel;

public sealed partial class AmmoWorkspaceViewModelTests
{
    // Penetration 20, 35, 48 and 60: against class four the margins are -20, -5, 8 and 20, so only
    // the last two are rated good or better; class five leaves 48 at -2, which is fair and so
    // not a round that beats it, and 60 at 10.
    private static readonly int[] Penetrations = [20, 35, 48, 60];

    [Fact]
    public void AClassFilterKeepsOnlyTheRoundsTheServiceRatesGoodOrBetter()
    {
        var rounds = Rounds();

        var classFour = AmmoWorkspaceViewModel.Arrange(rounds, 4, AmmoSort.Rank);
        var classFive = AmmoWorkspaceViewModel.Arrange(rounds, 5, AmmoSort.Rank);
        var classSix = AmmoWorkspaceViewModel.Arrange(rounds, 6, AmmoSort.Rank);

        Assert.Equal(["Round 48", "Round 60"], classFour.Select(round => round.Name));
        Assert.Equal(["Round 60"], classFive.Select(round => round.Name));
        Assert.Equal(["Round 60"], classSix.Select(round => round.Name));
    }

    [Fact]
    public void AClassNoRoundBeatsLeavesTheTableEmptyRatherThanShowingTheLeastBad()
    {
        var weakOnly = Rounds().Where(round => round.PenetrationValue < 40).ToArray();

        Assert.Empty(AmmoWorkspaceViewModel.Arrange(weakOnly, 5, AmmoSort.Rank));
    }

    [Fact]
    public void An_ammo_rows_learn_line_is_the_intelligence_engines_explanation()
    {
        var round = Round("round", "Round", damage: 55, penetration: 35);
        var row = new AmmoRoundRowViewModel(round, false, null!);

        Assert.Equal(round.LearnModeExplanation, row.LearnReason);
    }

    [Fact]
    public void AnyArmorAndBestFirstLeaveTheServicesOrderAlone()
    {
        var rounds = Rounds().Reverse().ToArray();

        Assert.Equal(rounds.Select(round => round.Name), AmmoWorkspaceViewModel.Arrange(rounds, 0, AmmoSort.Rank).Select(round => round.Name));
    }

    [Fact]
    public void RoundsCanBeOrderedByPenetrationDamageOrName()
    {
        var rounds = new[]
        {
            Round("b", "Bravo", damage: 90, penetration: 20),
            Round("c", "Charlie", damage: 40, penetration: 60),
            Round("a", "Alpha", damage: 55, penetration: 35),
        };

        Assert.Equal(["Charlie", "Alpha", "Bravo"], AmmoWorkspaceViewModel.Arrange(rounds, 0, AmmoSort.Penetration).Select(round => round.Name));
        Assert.Equal(["Bravo", "Alpha", "Charlie"], AmmoWorkspaceViewModel.Arrange(rounds, 0, AmmoSort.Damage).Select(round => round.Name));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], AmmoWorkspaceViewModel.Arrange(rounds, 0, AmmoSort.Name).Select(round => round.Name));
    }

    [Fact]
    public async Task LoadingOpensTheFirstCaliberAndItsFirstRound()
    {
        var (page, workspace) = await LoadedAsync();

        await WaitUntilAsync(() => workspace.HasSelectedRound);

        Assert.True(workspace.HasCalibers);
        Assert.Equal(4, workspace.Rounds.Count);
        Assert.False(workspace.ShowsNoSelectedRound);
        Assert.Equal(page.SelectedRound!.Name, workspace.SelectedName);
        Assert.Equal("4 rounds", workspace.RoundCountLabel);
        Assert.True(workspace.Rounds[0].IsSelected);

        // The page writes the advice from the value its setter was handed. A choice made from
        // inside that setter's own change notification was overwritten by the "select a round" hint.
        Assert.NotEmpty(page.SelectedRound!.PracticalAdvice);
        Assert.Equal(page.SelectedRound.PracticalAdvice, workspace.SelectedAdvice);
        Assert.Equal(page.SelectedRound.Name, page.Advice.Length > 0 ? workspace.SelectedName : string.Empty);
    }

    [Fact]
    public async Task ChoosingAnArmorClassNarrowsTheTableAndMovesTheChoiceOffAHiddenRound()
    {
        var (page, workspace) = await LoadedAsync();
        await WaitUntilAsync(() => workspace.HasSelectedRound);
        var raised = new List<string?>();
        workspace.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        // The service ranks best first, so the round chosen on load is the 60 penetration one;
        // choose the weakest by hand, then filter it away.
        page.SelectedRound = page.Rounds.Single(round => round.PenetrationValue == 20);
        workspace.ArmorFilters.Single(chip => chip.Key == "class-4").SelectCommand.Execute(null);

        Assert.Equal(4, workspace.ArmorClass);
        Assert.Equal(2, workspace.Rounds.Count);
        Assert.Equal("2 of 4 rounds", workspace.RoundCountLabel);
        Assert.Contains(nameof(AmmoWorkspaceViewModel.Rounds), raised);
        Assert.True(workspace.ArmorFilters.Single(chip => chip.Key == "class-4").IsSelected);
        Assert.NotEqual(20, page.SelectedRound!.PenetrationValue);
        Assert.Single(workspace.Rounds, row => row.IsSelected);
    }

    [Fact]
    public async Task AClassNothingBeatsSaysSoInsteadOfGoingBlank()
    {
        var (page, workspace) = await LoadedAsync(penetrations: [20, 35]);
        await WaitUntilAsync(() => workspace.HasSelectedRound);

        workspace.ArmorFilters.Single(chip => chip.Key == "class-6").SelectCommand.Execute(null);

        Assert.True(workspace.ShowsNoRounds);
        Assert.Equal("No round in this caliber beats class 6.", workspace.NoRoundsLabel);
        Assert.Equal("0 of 2 rounds", workspace.RoundCountLabel);
        Assert.Null(page.SelectedRound);

        // Any armor brings the whole caliber back, and a round is chosen again.
        workspace.ArmorFilters.Single(chip => chip.Key == "class-any").SelectCommand.Execute(null);
        await WaitUntilAsync(() => workspace.HasSelectedRound);
        Assert.Equal(2, workspace.Rounds.Count);
        Assert.False(workspace.ShowsNoRounds);
    }

    [Fact]
    public async Task OpeningInIntelHandsOverTheChosenRoundsItemId()
    {
        string? opened = null;
        var (page, workspace) = await LoadedAsync(id => opened = id);
        await WaitUntilAsync(() => workspace.HasSelectedRound);

        workspace.OpenInIntelCommand.Execute(null);

        Assert.Equal(page.SelectedRound!.ItemId, opened);
    }

    /// <summary>
    /// #283: what a case scan counted reaches the Ammo page, loose rounds plus sealed packs, and a
    /// round nobody counted says so rather than "none".
    /// </summary>
    [Fact]
    public async Task OwnedRoundsFromAScanShowOnTheCaliberTheRoundAndTheContextPanel()
    {
        var profile = new OwnedCountsProfile(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["round-60"] = 35,
            ["pack-60"] = 2,
            ["round-20"] = 0,
        });
        var (page, workspace) = await LoadedAsync(profiles: profile, packs: [new("pack-60", "round-60", 30, Provenance)]);
        await WaitUntilAsync(() => workspace.HasRounds);

        Assert.Equal("95 owned", Assert.Single(workspace.Calibers).Owned);
        var rows = workspace.Rounds.ToDictionary(row => row.Round.ItemId);
        Assert.Equal("95 owned", rows["round-60"].Owned);
        Assert.Equal("None owned", rows["round-20"].Owned);
        Assert.False(rows["round-35"].HasOwned);

        page.SelectedRound = rows["round-60"].Round;
        Assert.Equal("You own 95 rounds.", workspace.SelectedOwned);
        page.SelectedRound = rows["round-35"].Round;
        Assert.Equal("Owned: not scanned. Stash › Ammo cases.", workspace.SelectedOwned);

        // A scan lands while the page is open; showing the page again re-reads it.
        profile.Owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["round-35"] = 120 };
        await workspace.LoadOwnedAsync();
        Assert.Equal("You own 120 rounds.", workspace.SelectedOwned);
        Assert.Equal("120 owned", Assert.Single(workspace.Calibers).Owned);
    }

    [Fact]
    public void Without_a_profile_the_page_says_nothing_about_ownership()
    {
        var owned = new OwnedAmmo(new Dictionary<string, int>(StringComparer.Ordinal), []);

        Assert.Null(owned.RoundsOf("round"));
        Assert.Null(owned.RoundsOf(["a", "b"]));
        Assert.Equal(string.Empty, OwnedAmmo.Short(null));
    }

    private static async Task<(AmmoPageViewModel Page, AmmoWorkspaceViewModel Workspace)> LoadedAsync(
        Action<string>? openItem = null,
        int[]? penetrations = null,
        TarkovCompanion.Core.Abstractions.IPlayerProfileService? profiles = null,
        AmmoPackContents[]? packs = null)
    {
        penetrations ??= Penetrations;
        var stats = penetrations
            .Select(penetration => new AmmoStats(
                $"round-{penetration}", "Caliber556x45NATO", 50, penetration, 40, 0.1, 1, null, null, null, false, false, Provenance))
            .ToArray();
        var page = new AmmoPageViewModel(
            new FakeFactCatalog { Ammo = stats, Packs = packs ?? [] },
            new FakeItemRepository(penetrations.Select(penetration => Item($"round-{penetration}", $"Round {penetration}")).ToArray()),
            profiles);
        var workspace = new AmmoWorkspaceViewModel(page, openItem);

        await page.LoadAsync();
        return (page, workspace);
    }

    private static AmmoRoundViewModel[] Rounds() => [.. Penetrations.Select(penetration => Round($"round-{penetration}", $"Round {penetration}", 50, penetration))];

    // The chips are built the way AmmoPageViewModel builds them: margin against armor class x 10.
    private static AmmoRoundViewModel Round(string id, string name, int damage, int penetration) => new(
        id,
        name,
        "#1 of 1",
        "Tier",
        damage.ToString(),
        penetration.ToString(),
        "40%",
        "10%",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        [
            .. Enumerable.Range(1, 6).Select(armorClass =>
            {
                var margin = penetration - (armorClass * 10);
                return new AmmoArmorRatingViewModel($"Class {armorClass}", "rating", margin >= 0, margin is < 0 and >= -10, margin < -10) { ClassNumber = armorClass };
            }),
        ])
    {
        DamageValue = damage,
        PenetrationValue = penetration,
    };
}
