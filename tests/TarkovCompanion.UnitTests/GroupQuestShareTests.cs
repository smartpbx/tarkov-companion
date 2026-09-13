using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the group is told the player is working on.
/// </summary>
/// <remarks>
/// The quest switch on the Group page sent an empty list from the day it was built, because
/// there was no quest progress to send. Now that the game's own notifications record every
/// quest starting and being handed in, there is.
/// </remarks>
public sealed class GroupQuestShareTests
{
    [Fact]
    public async Task PinnedComesBeforeActive()
    {
        var share = Share(
            Quest("Debut", RecordedTaskState.Active),
            Quest("Zhivchik", RecordedTaskState.Active, isPinned: true));

        Assert.Equal(["Zhivchik", "Debut"], await share.GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// A group asks what you are on, not what you have done.
    /// </summary>
    [Theory]
    [InlineData(RecordedTaskState.Completed)]
    [InlineData(RecordedTaskState.Failed)]
    [InlineData(RecordedTaskState.NotStarted)]
    [InlineData(RecordedTaskState.Unknown)]
    public async Task FinishedAndUnstartedQuestsAreNotShared(RecordedTaskState state)
    {
        var share = Share(Quest("Debut", state));

        Assert.Empty(await share.GetAsync(CancellationToken.None));
    }

    /// <summary>A pinned quest is shared whatever state it is recorded in.</summary>
    [Fact]
    public async Task APinnedQuestIsSharedEvenWhenItIsNotActive()
    {
        var share = Share(Quest("Debut", RecordedTaskState.NotStarted, isPinned: true));

        Assert.Equal(["Debut"], await share.GetAsync(CancellationToken.None));
    }

    /// <summary>Five is what fits in a squadmate's panel.</summary>
    [Fact]
    public async Task NoMoreThanFive()
    {
        var share = Share(Enumerable.Range(0, 9)
            .Select(index => Quest("Quest " + index, RecordedTaskState.Active))
            .ToArray());

        Assert.Equal(5, (await share.GetAsync(CancellationToken.None)).Count);
    }

    /// <summary>
    /// The board is read at most once a minute, because the publish loop is not.
    /// </summary>
    [Fact]
    public async Task TheBoardIsNotReadOnEveryExchange()
    {
        var reads = new CountingQuests(Quest("Debut", RecordedTaskState.Active));
        var share = new GroupQuestShare(new StubProfiles(), reads);

        await share.GetAsync(CancellationToken.None);
        await share.GetAsync(CancellationToken.None);
        await share.GetAsync(CancellationToken.None);

        Assert.Equal(1, reads.Reads);
    }

    /// <summary>A group exchange must not fail because the quest board could not be read.</summary>
    [Fact]
    public async Task AnUnreadableBoardSharesNothingRatherThanThrowing()
    {
        var share = new GroupQuestShare(new StubProfiles(), new ThrowingQuests());

        Assert.Empty(await share.GetAsync(CancellationToken.None));
    }

    private static GroupQuestShare Share(params QuestSummaryReadModel[] tasks) =>
        new(new StubProfiles(), new CountingQuests(tasks));

    private static QuestSummaryReadModel Quest(string name, RecordedTaskState state, bool isPinned = false) => new(
        name.ToLowerInvariant(),
        name,
        null,
        null,
        state,
        "Manual",
        null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        isPinned,
        null,
        false,
        [],
        [],
        []);

    private sealed class CountingQuests(params QuestSummaryReadModel[] tasks) : IQuestReadService
    {
        public int Reads { get; private set; }

        public Task<QuestBoardReadModel> GetQuestBoardAsync(
            QuestProfileScope scope,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(
                new QuestBoardReadModel(scope, 1, null, tasks, []));
        }

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
            QuestProfileScope scope,
            string itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingQuests : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(
            QuestProfileScope scope,
            CancellationToken cancellationToken) => throw new InvalidOperationException("no catalog");

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
            QuestProfileScope scope,
            string itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                Guid.Empty,
                "Local profile",
                GameMode.Regular,
                1,
                Faction.Unknown,
                null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, EventItemState>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
