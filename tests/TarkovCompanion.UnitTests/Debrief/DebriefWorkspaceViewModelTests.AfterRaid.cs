using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Debrief;

/// <summary>[#712 0-8] The after-raid card: asked once per raid end, answered with Manual provenance, never guessed.</summary>
public sealed partial class DebriefWorkspaceViewModelTests
{
    private static readonly Guid EndedRaidId = Guid.Parse("40000000-0000-0000-0000-000000000099");

    [Fact]
    public async Task A_raid_end_signal_opens_the_question_for_that_raid_with_its_recap()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(EndedRaid(outcome: null));
        var signal = new FakeRaidEndSignal();
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths(), raidEnds: signal, dispatch: action => action());

        Assert.False(viewModel.AfterRaid.IsVisible);
        signal.Raise(Ended());
        await WaitUntil(() => viewModel.AfterRaid.HasRecap);

        Assert.True(viewModel.AfterRaid.IsVisible);
        Assert.True(viewModel.AfterRaid.IsAsking);
        Assert.Equal(EndedRaidId, viewModel.AfterRaid.RaidId);
        Assert.Equal(4, viewModel.AfterRaid.Choices.Count);
        Assert.Contains(viewModel.AfterRaid.Recap, line => line.Text == "24m 00s in raid · customs" && line.KindLabel == "Observed");
        Assert.Contains(viewModel.AfterRaid.Recap, line => line.Text == "Squad: Geo" && line.KindLabel == "Observed");
        // Nothing about the extract was recorded or can be placed, so the recap says so and claims no source.
        Assert.Contains(viewModel.AfterRaid.Recap, line => line.Text == "Extract not recorded" && !line.HasKind);
    }

    [Fact]
    public async Task One_tap_stores_the_answer_as_the_players_manual_outcome_and_closes_the_question()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(EndedRaid(outcome: null, notes: "Dorms"));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.OfferAfterRaidAsync(Ended(), CancellationToken.None);

        await Press(viewModel.AfterRaid.Choices.Single(choice => choice.Bucket == RaidOutcomeBucket.Died));

        Assert.Equal(("Died", "Dorms"), service.LastCorrection);
        var answer = Assert.Single(RaidOutcomeAnswer.ParseAll(
            await service.ListEventPayloadsAsync(EndedRaidId, RaidOutcomeAnswer.EventType, CancellationToken.None)));
        Assert.Equal("Died", answer.Outcome);
        Assert.Equal(RaidOutcomeAnswer.PlayerSource, answer.Source);
        Assert.False(viewModel.AfterRaid.IsAsking);
        Assert.StartsWith("Died · you said so at ", viewModel.AfterRaid.OutcomeLabel, StringComparison.Ordinal);
        Assert.Equal("Manual", viewModel.AfterRaid.OutcomeKindLabel);
        var row = Assert.Single(viewModel.Raids);
        Assert.Equal("Died", row.Outcome);
        Assert.Equal("Manual", row.OutcomeKindLabel);

        // The same raid's end reported again does not ask a second time.
        await viewModel.OfferAfterRaidAsync(Ended(), CancellationToken.None);
        Assert.False(viewModel.AfterRaid.IsAsking);
    }

    [Fact]
    public async Task A_raid_that_already_has_an_outcome_is_not_asked_about_and_keeps_it()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(EndedRaid(outcome: "Survived"));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.OfferAfterRaidAsync(Ended(), CancellationToken.None);

        Assert.True(viewModel.AfterRaid.IsVisible);
        Assert.False(viewModel.AfterRaid.IsAsking);
        Assert.Equal("Survived", viewModel.AfterRaid.OutcomeLabel);
        Assert.Null(service.LastCorrection);
    }

    [Fact]
    public async Task Later_leaves_the_outcome_unrecorded_and_the_raid_is_not_asked_about_again()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(EndedRaid(outcome: null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.OfferAfterRaidAsync(Ended(), CancellationToken.None);

        await Press(viewModel.AfterRaid.LaterCommand);

        Assert.False(viewModel.AfterRaid.IsAsking);
        Assert.Equal("Outcome not recorded", viewModel.AfterRaid.OutcomeLabel);
        Assert.False(viewModel.AfterRaid.HasOutcomeKind);
        Assert.Null(service.LastCorrection);
        Assert.True(Assert.Single(RaidOutcomeAnswer.ParseAll(
            await service.ListEventPayloadsAsync(EndedRaidId, RaidOutcomeAnswer.EventType, CancellationToken.None))).Dismissed);

        var reopened = new DebriefWorkspaceViewModel(service, TestPaths());
        await reopened.OfferAfterRaidAsync(Ended(), CancellationToken.None);
        Assert.False(reopened.AfterRaid.IsAsking);
    }

    [Fact]
    public async Task A_tap_never_overwrites_an_outcome_recorded_after_the_card_opened()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(EndedRaid(outcome: null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.OfferAfterRaidAsync(Ended(), CancellationToken.None);
        service.Seed(EndedRaid(outcome: "MIA"));

        await Press(viewModel.AfterRaid.Choices.Single(choice => choice.Bucket == RaidOutcomeBucket.Survived));

        Assert.Null(service.LastCorrection);
        Assert.Equal("Outcome: MIA", viewModel.AfterRaid.OutcomeLabel);
    }

    private static RaidHistoryEntry EndedRaid(string? outcome, string? notes = null) =>
        new(EndedRaidId, Guid.NewGuid(), "customs", "Regular", Started, Started.AddMinutes(24), outcome, notes);

    private static RaidEnded Ended() => new(EndedRaidId, "customs", "PMC", Started, Started.AddMinutes(24), ["Geo"]);

    private static async Task Press(RaidOutcomeChoiceViewModel choice) => await Press(choice.Command);

    private static async Task Press(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        // The commands are async; the fakes complete synchronously, so one yield settles them.
        await Task.Yield();
        await Task.Delay(10);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeRaidEndSignal : IRaidEndSignal
    {
        public event EventHandler<RaidEnded>? RaidEnded;

        public void Raise(RaidEnded ended) => RaidEnded?.Invoke(this, ended);
    }
}
