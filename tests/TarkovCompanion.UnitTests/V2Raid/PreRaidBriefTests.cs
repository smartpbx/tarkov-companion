using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[#712 0-9] The pre-raid brief: built from a Matching/Loading situation, bosses as catalog chances.</summary>
public sealed class PreRaidBriefTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 20, 14, 7, TimeSpan.Zero);

    private static Situation At(SituationPhase phase, string? map, SituationSide side, params SituationSquadMember[] squad) =>
        new(7, Now, new SituationFact<SituationPhase>(phase, new Confidence(0.9), SituationSource.GameLog, Now, "You pressed Ready at 20:14."))
        {
            Map = map is null ? null : new SituationFact<string>(map, new Confidence(0.9), SituationSource.GameLog, Now, "The log named the map."),
            Side = new SituationFact<SituationSide>(side, new Confidence(0.9), SituationSource.GameLog, Now, "From the game log."),
            Squad = squad,
        };

    private static readonly MapBossChance[] CustomsBosses =
    [
        new("bossBully", "Reshala", 0.6, false),
        new("bossKnight", "Knight", 0.2, false),
        new("sectantPriest", "Cultist Priest", 0.2, false),
        new("bossPartisan", "Partisan", 0.15, false),
    ];

    [Fact]
    public void A_pmc_in_the_customs_queue_gets_the_map_side_length_bosses_quests_and_extracts()
    {
        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Matching, "customs", SituationSide.Pmc,
            new SituationSquadMember("Geo", SquadMemberState.OutOfRaid, null, null, null, null, null, "relay")))
        {
            MapName = "Customs",
            RaidLength = TimeSpan.FromMinutes(40),
            Bosses = CustomsBosses,
            ListsMapId = "customs",
            Quests =
            [
                new("customs", "Checking", "Find the bronze pocket watch", "", false),
                new("customs", "Golden Swag", "Hide the lighter in Dorms", "Dorm room 214 key", true),
                new("woods", "Shootout Picnic", "Kill scavs on Woods", "", true),
            ],
            Extracts =
            [
                new("ZB-1011", "", false),
                new("Crossroads", "", false),
                new("Dorms V-Ex", "Costs 7,000 ₽", false),
                new("Customs to Factory", "", true),
            ],
        });

        Assert.True(brief.IsShown);
        Assert.Equal("BRIEF · WHILE YOU MATCH", brief.Kicker);
        Assert.Equal("Customs · PMC · 40 min", brief.Title);
        // Catalog chances, labelled as such and never as a sighting; three at most on one line.
        Assert.Equal("Reshala 60% · Knight 20% · Cultist Priest 20% · possible, from the catalog", brief.Bosses);
        // Only this map's quests, the pinned one first.
        Assert.Equal(new[] { "Golden Swag", "Checking" }, brief.Quests.Select(quest => quest.Task));
        Assert.Equal("needs: Dorm room 214 key", brief.Quests[0].Needs);
        Assert.True(brief.CanStillLeave);
        Assert.Equal("Extracts for PMC", brief.ExtractsHeading);
        Assert.Equal("ZB-1011 · Crossroads · Dorms V-Ex", brief.Extracts);
        Assert.Equal(new[] { "Dorms V-Ex · Costs 7,000 ₽" }, brief.ExtractRequirements);
        Assert.Equal(new[] { "Geo · not in raid" }, brief.Squad);
        Assert.Equal("You pressed Ready at 20:14.", brief.Because);
    }

    [Fact]
    public void A_scav_loading_into_reserve_gets_scav_extracts_and_a_triggered_boss_says_so()
    {
        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Loading, "reserve", SituationSide.Scav))
        {
            MapName = "Reserve",
            RaidLength = TimeSpan.FromMinutes(33),
            Bosses = [new("PmcBot", "Raider", 0.4, false), new("bossGluhar", "Glukhar", 0.3, true)],
            ListsMapId = "reserve",
            Extracts = [new("Scav Lands", "", false), new("Sewer Manhole", "No backpack", false)],
        });

        Assert.Equal("BRIEF · WHILE IT LOADS", brief.Kicker);
        Assert.Equal("Reserve · Scav · 33 min", brief.Title);
        Assert.Equal("Raider 40% · Glukhar 30% on a trigger · possible, from the catalog", brief.Bosses);
        Assert.Equal("Extracts for Scav", brief.ExtractsHeading);
        Assert.Equal("No active quests on this map", brief.QuestsNote);
        // Loading is past the point of backing out.
        Assert.False(brief.CanStillLeave);
    }

    [Fact]
    public void Lists_built_for_another_map_are_dropped_rather_than_shown_under_this_one()
    {
        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Matching, "customs", SituationSide.Unknown))
        {
            ListsMapId = "woods",
            Quests = [new("woods", "Shootout Picnic", "Kill scavs", "", false)],
            Extracts = [new("Outskirts", "", false)],
        });

        Assert.Empty(brief.Quests);
        Assert.Equal("No extracts listed for this map", brief.Extracts);
        Assert.Equal("Extracts · side unknown, both shown", brief.ExtractsHeading);
        Assert.Equal("customs · side unknown", brief.Title);
    }

    [Theory]
    [InlineData(SituationPhase.Menu)]
    [InlineData(SituationPhase.InRaid)]
    [InlineData(SituationPhase.PostRaid)]
    public void Outside_the_queue_and_the_loading_screen_there_is_no_brief(SituationPhase phase)
    {
        Assert.False(PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(phase, "customs", SituationSide.Pmc))).IsShown);
    }

    [Fact]
    public void A_queue_without_a_map_line_yet_has_no_brief() =>
        Assert.False(PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Matching, null, SituationSide.Pmc))).IsShown);

    [Fact]
    public void Bosses_not_read_yet_leave_the_line_empty_instead_of_saying_there_are_none()
    {
        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Matching, "customs", SituationSide.Pmc)) { BossesRead = false });
        Assert.False(brief.HasBosses);
    }

    [Fact]
    public void The_catalog_payload_gives_one_row_per_boss_at_its_highest_chance_with_the_language_tables_name()
    {
        const string payload = """
            {"normalizedName":"reserve","bosses":[
              {"mob":"PmcBot","spawnChance":0.3,"spawnTrigger":"Switch"},
              {"mob":"PmcBot","spawnChance":0.4,"spawnTrigger":null},
              {"mob":"bossGluhar","spawnChance":0.35},
              {"mob":"exUsecFree","spawnChance":0.5},
              {"mob":"bossNobody","spawnChance":0}
            ]}
            """;
        var names = MapBossChances.Names("""{"data":{"PmcBot":"Raider","bossGluhar":"Glukhar","exUsecFree":"exUsecFree","ExUsec":"Rogue"}}""");

        var bosses = MapBossChances.Read(payload, names);

        Assert.Equal(new[] { "Rogue", "Raider", "Glukhar" }, bosses.Select(boss => boss.Name));
        Assert.Equal(new[] { 0.5, 0.4, 0.35 }, bosses.Select(boss => boss.Chance));
        Assert.False(bosses[1].IsTriggered);
        Assert.Empty(MapBossChances.Read("not json", names));
    }
}

/// <summary>[#712 0-9] The brief never scrolls: the fullest brief fits the Raid panel at 1920x1080.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PreRaidBriefFitTests
{
    [Theory]
    [InlineData(360)]
    [InlineData(300)]
    public async Task The_fullest_brief_fits_the_raid_panel_at_1080p_without_a_scroll(double panelWidth)
    {
        var tall = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(
            new Situation(1, DateTimeOffset.UnixEpoch, new(SituationPhase.Matching, new Confidence(0.9), SituationSource.GameLog, DateTimeOffset.UnixEpoch,
                "You pressed Ready at 20:14 and the game is looking for a raid, with a long reason that wraps."))
            {
                Map = new("streets-of-tarkov", new Confidence(0.9), SituationSource.GameLog, DateTimeOffset.UnixEpoch, "log"),
                Side = new(SituationSide.Pmc, new Confidence(0.9), SituationSource.GameLog, DateTimeOffset.UnixEpoch, "log"),
                Next = new("a", "Golden Zibbo lighter in the Dorms three-story second floor room", 310, "streets-of-tarkov", DateTimeOffset.UnixEpoch, "plan"),
                Then = new("b", "Bronze pocket watch in the Big Red warehouse office", 180, "streets-of-tarkov", DateTimeOffset.UnixEpoch, "plan"),
                Squad = [.. Enumerable.Range(1, 6).Select(i => new SituationSquadMember($"Squadmate{i}", SquadMemberState.OnAnotherMap, null, null, null, null, null, "relay"))],
            })
        {
            MapName = "Streets of Tarkov",
            RaidLength = TimeSpan.FromMinutes(50),
            Bosses = [new("bossKolontay", "Kollontay", 0.4, false), new("bossKilla", "Vengeful Killa", 0.3, true), new("bossKnight", "Knight", 0.2, false)],
            ListsMapId = "streets-of-tarkov",
            Quests = [.. Enumerable.Range(1, 8).Select(i => new PreRaidBriefQuest(
                "streets-of-tarkov",
                $"A quest with a long name number {i} (Mechanic)",
                "Locate and mark the first vehicle with an MS2000 marker · Survive and extract · Hand over the item",
                "Dorm room 314 marked key, MS2000 Marker ×2, Pack of sugar, Gas analyzer, Bottle of Tarkovskaya vodka",
                i == 1))],
            Extracts = [.. Enumerable.Range(1, 14).Select(i => new PreRaidBriefExtract(
                $"Extract number {i} by the long road",
                "Needs power: Switch at the Concordia lobby → Generator in the basement · Costs 7,000 ₽ · No backpack",
                false))],
            LootSpots = 50,
            LootSpotsCapped = true,
        });

        // The Raid panel's height at 1920x1080: the window less the top bar (64) and the panel's margins.
        const double available = 1080 - 64 - 16;
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        var measured = await session.Dispatch(
            () =>
            {
                var model = new PreRaidBriefViewModel();
                model.Show(tall);
                var view = new PreRaidBriefView { DataContext = model };
                var window = new Window { Width = panelWidth, Height = available, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    view.Measure(new Size(panelWidth, double.PositiveInfinity));
                    return view.DesiredSize.Height;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.True(measured <= available, $"The fullest brief needs {measured:0} px at {panelWidth} px wide; the panel has {available:0}.");
    }
}
