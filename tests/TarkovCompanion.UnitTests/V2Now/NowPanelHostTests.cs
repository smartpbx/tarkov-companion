using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>[#712 0-4] The Raid page's right column: the flag picks the Now panel or the cards, and More reaches every card.</summary>
public sealed class NowPanelHostTests
{
    [Fact]
    public void With_the_flag_off_the_column_is_the_Raid_plan_cards_as_before()
    {
        using var panel = new NowPanelViewModel(null, tick: false);
        var host = new NowPanelHost(() => false) { Panel = panel };

        Assert.False(host.ShowsNowPanel);
        Assert.True(host.ShowsCardStack);
        Assert.False(host.IsMoreOpen);
    }

    [Fact]
    public void With_the_flag_on_but_no_situation_yet_the_cards_stay()
    {
        var host = new NowPanelHost(() => true);

        Assert.False(host.ShowsNowPanel);
        Assert.True(host.ShowsCardStack);
    }

    [Fact]
    public void With_the_flag_on_the_Now_panel_is_the_column_and_More_opens_the_cards_and_comes_back()
    {
        var flag = true;
        var revealed = new List<NowMoreTopic>();
        using var panel = new NowPanelViewModel(null, tick: false);
        var host = new NowPanelHost(() => flag, revealed.Add) { Panel = panel };
        var opened = new List<NowMoreTopic>();
        host.MoreOpened += (_, topic) => opened.Add(topic);
        Assert.True(host.ShowsNowPanel);

        panel.WrongCommand.Execute(null);

        Assert.True(host.IsMoreOpen);
        Assert.True(host.ShowsCardStack);
        Assert.False(host.ShowsNowPanel);
        Assert.Equal([NowMoreTopic.Corrections], revealed);
        Assert.Equal([NowMoreTopic.Corrections], opened);

        host.CloseMoreCommand.Execute(null);
        Assert.True(host.ShowsNowPanel);

        panel.MoreCommand.Execute(null);
        Assert.True(host.IsMoreOpen);
        Assert.Equal([NowMoreTopic.Corrections], revealed);

        flag = false;
        host.Refresh();
        Assert.False(host.IsMoreOpen);
        Assert.True(host.ShowsCardStack);
    }

    [Fact]
    public void Every_card_the_Raid_plan_had_is_in_the_More_drawer_under_a_topic()
    {
        var cards = new RaidPanelCards(null);
        var ids = typeof(RaidPanelCards).GetProperties()
            .Where(property => property.PropertyType == typeof(RaidPanelCardViewModel))
            .Select(property => ((RaidPanelCardViewModel)property.GetValue(cards)!).Id)
            .ToArray();

        Assert.Equal(16, ids.Length);
        Assert.All(ids, id => Assert.True(NowPanelHost.TopicOfCard.ContainsKey(id), $"card '{id}' has no More topic"));
        Assert.Equal(ids.Order(StringComparer.Ordinal), NowPanelHost.TopicOfCard.Keys.Order(StringComparer.Ordinal));
        Assert.All(
            Enum.GetValues<NowMoreTopic>().Where(topic => topic != NowMoreTopic.All),
            topic => Assert.NotEmpty(NowPanelHost.CardsOf(topic)));
    }

    [Fact]
    public void A_squadmate_moving_area_pulses_their_row_and_a_new_row_does_not()
    {
        using var scope = NowPanelStateTests.English();
        using var panel = new NowPanelViewModel(null, post: action => action(), tick: false);
        var pulsed = new List<string>();
        panel.SquadPulsed += (_, row) => pulsed.Add(row.Name);
        var situation = NowPanelStateTests.InRaid(19, 35);

        panel.Show(situation);
        Assert.Equal(["Geo", "Riley", "Sam"], panel.SquadRows.Select(row => row.Name));
        Assert.Empty(pulsed);
        Assert.All(panel.SquadRows, row => Assert.False(row.IsPulsing));

        var geo = situation.Squad[0] with { AreaName = "Dorms 3-story", DistanceMetres = 30 };
        panel.Show(situation with { Squad = [geo, situation.Squad[1], situation.Squad[2]] });

        Assert.Equal(["Geo"], pulsed);
        Assert.True(panel.SquadRows[0].IsPulsing);
        Assert.False(panel.SquadRows[1].IsPulsing);

        // Only the distance moved: not worth a pulse.
        panel.Show(situation with { Squad = [geo with { DistanceMetres = 35 }, situation.Squad[1]] });
        Assert.Equal(["Geo"], pulsed);
        Assert.Equal(["Geo", "Riley"], panel.SquadRows.Select(row => row.Name));
    }

    [Fact]
    public async Task Ping_from_a_row_asks_for_that_squadmate_by_name_and_pulses_the_row()
    {
        using var scope = NowPanelStateTests.English();
        var asked = new List<string>();
        using var panel = new NowPanelViewModel(null, post: action => action(), tick: false)
        {
            PingMember = (name, _) =>
            {
                asked.Add(name);
                return Task.FromResult(true);
            },
        };
        panel.Show(NowPanelStateTests.InRaid(19, 35));

        await ((TarkovCompanion.App.ViewModels.AsyncDelegateCommand)panel.SquadRows[0].PingCommand).ExecuteAsync();
        panel.SquadRows[1].PingCommand.Execute(null);

        Assert.Equal(["Geo"], asked);
        Assert.True(panel.SquadRows[0].IsPulsing);
    }

    [Fact]
    public void The_last_phase_change_reason_is_the_because_line()
    {
        using var scope = NowPanelStateTests.English();
        var state = NowPanelState.Project(NowPanelStateTests.Out(SituationPhase.Menu), DateTimeOffset.UnixEpoch, because: "The last raid ended at 14:02.");

        Assert.Equal("The last raid ended at 14:02.", state.Because);
    }
}
