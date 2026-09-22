using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What is done with the game's word about a quest.
/// </summary>
/// <remarks>
/// Both rules here exist because a log is replayed rather than streamed: the same files are
/// re-read from the start whenever observation restarts, and the game restates a notification
/// when it redelivers one.
/// </remarks>
public sealed class QuestLogProgressServiceTests
{
    [Fact]
    public async Task WhatTheGameSaysIsRecorded()
    {
        var commands = new RecordingCommands();
        var service = Service(commands);

        await service.ApplyAsync(Observed("e1", "task-1", RecordedTaskState.Completed), CancellationToken.None);

        var recorded = Assert.Single(commands.Calls);
        Assert.Equal("task-1", recorded.TaskId);
        Assert.Equal(RecordedTaskState.Completed, recorded.State);
        Assert.Equal(1, service.Recorded);
    }

    /// <summary>The same message arriving again is the same message.</summary>
    [Fact]
    public async Task ARedeliveredMessageIsNotActedOnTwice()
    {
        var commands = new RecordingCommands();
        var service = Service(commands);

        await service.ApplyAsync(Observed("e1", "task-1", RecordedTaskState.Active), CancellationToken.None);
        await service.ApplyAsync(Observed("e1", "task-1", RecordedTaskState.Active), CancellationToken.None);

        Assert.Single(commands.Calls);
    }

    /// <summary>
    /// A quest that has been handed in does not go back to started.
    /// </summary>
    /// <remarks>
    /// An old "started" arriving after a hand-in would undo it, and somebody watching their
    /// completed list empty itself would rightly stop trusting the page.
    /// </remarks>
    [Theory]
    [InlineData(RecordedTaskState.Completed)]
    [InlineData(RecordedTaskState.Failed)]
    public async Task AQuestNeverGoesBackwards(RecordedTaskState finished)
    {
        var commands = new RecordingCommands();
        var service = Service(commands);

        await service.ApplyAsync(Observed("e1", "task-1", finished), CancellationToken.None);
        await service.ApplyAsync(Observed("e2", "task-1", RecordedTaskState.Active), CancellationToken.None);

        Assert.Equal(finished, Assert.Single(commands.Calls).State);
    }

    [Fact]
    public async Task AQuestThatIsStartedCanStillBeHandedIn()
    {
        var commands = new RecordingCommands();
        var service = Service(commands);

        await service.ApplyAsync(Observed("e1", "task-1", RecordedTaskState.Active), CancellationToken.None);
        await service.ApplyAsync(Observed("e2", "task-1", RecordedTaskState.Completed), CancellationToken.None);

        Assert.Equal(2, commands.Calls.Count);
        Assert.Equal(RecordedTaskState.Completed, commands.Calls[^1].State);
    }

    [Fact]
    public async Task AQuestSyncedActiveStillAcceptsALaterGameLogCompletion()
    {
        var commands = new RecordingCommands();
        var service = Service(commands);
        var scope = new QuestProfileScope(Guid.Empty, GameMode.Regular, "legacy");
        await commands.SetTaskStateAsync(scope, "task-1", RecordedTaskState.Active, CancellationToken.None);

        await service.ApplyAsync(Observed("game-finished", "task-1", RecordedTaskState.Completed), CancellationToken.None);

        Assert.Equal(RecordedTaskState.Completed, commands.Calls[^1].State);
        Assert.Equal(2, commands.Calls.Count);
    }

    /// <summary>One unrecordable quest is one quest, not the end of reading the logs.</summary>
    [Fact]
    public async Task AFailedWriteDoesNotThrow()
    {
        var service = Service(new ThrowingCommands());

        await service.ApplyAsync(Observed("e1", "task-1", RecordedTaskState.Completed), CancellationToken.None);

        Assert.Equal(0, service.Recorded);
    }

    private static QuestLogProgressService Service(IQuestProgressCommandService commands) =>
        new(new StubProfiles(), commands, NullLogger<QuestLogProgressService>.Instance);

    private static QuestStatusObservation Observed(string eventId, string taskId, RecordedTaskState state) =>
        new(eventId, taskId, state, DateTimeOffset.UnixEpoch);

    private sealed class RecordingCommands : StubCommands
    {
        public List<(string TaskId, RecordedTaskState State)> Calls { get; } = [];

        public override Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            CancellationToken cancellationToken)
        {
            Calls.Add((taskId, state));
            return Task.FromResult(new QuestProgressCommandResult(Guid.NewGuid(), Calls.Count, true));
        }
    }

    private sealed class ThrowingCommands : StubCommands
    {
        public override Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no storage");
    }

    private class StubCommands : IQuestProgressCommandService
    {
        public virtual Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QuestProgressCommandResult(Guid.NewGuid(), 1, true));

        public Task<QuestProgressCommandResult> SetObjectiveProgressAsync(
            QuestProfileScope scope,
            string objectiveId,
            RecordedObjectiveState state,
            decimal? count,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetItemHoldingAsync(
            QuestProfileScope scope,
            string itemId,
            bool foundInRaid,
            int? count,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetPinAsync(
            QuestProfileScope scope,
            QuestPinTargetKind targetKind,
            string targetId,
            bool isPinned,
            int sortOrder,
            string? note,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
