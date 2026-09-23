using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Stash;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashReviewCommandPersistenceTests
{
    [Fact]
    public async Task Review_commands_reload_from_a_new_store_instance_after_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stash-review-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var command = new StashReviewCommand(
                Guid.Parse("be282545-5b69-47ae-a465-fdf887118636"),
                "recognition-snapshot",
                StashReviewActionKind.CorrectQuantity,
                ["stash/5/8"],
                new DateTimeOffset(2026, 9, 22, 12, 34, 56, TimeSpan.Zero),
                "v2.stash-workspace",
                correctedQuantity: 3,
                reason: "Counted on review.");

            await new SqliteStashReviewCommandStore(factory)
                .AppendAsync(command, CancellationToken.None);

            var replayed = Assert.Single(await new SqliteStashReviewCommandStore(factory)
                .ListAsync(command.SnapshotId, CancellationToken.None));
            Assert.Equal(command.CommandId, replayed.CommandId);
            Assert.Equal(command.SnapshotId, replayed.SnapshotId);
            Assert.Equal(command.Action, replayed.Action);
            Assert.Equal(command.TargetItemKeys, replayed.TargetItemKeys);
            Assert.Equal(command.CreatedUtc, replayed.CreatedUtc);
            Assert.Equal(command.CorrectedQuantity, replayed.CorrectedQuantity);
            Assert.Equal(command.Reason, replayed.Reason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }

    [Fact]
    public async Task PlanCommandsAndTheirUndosReloadFromSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stash-plan-commands-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var now = new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero);
            var current = Guid.Parse("30000000-0000-0000-0000-000000000001");
            var previous = Guid.Parse("30000000-0000-0000-0000-000000000002");
            StashReviewCommand New(StashReviewActionKind action, IReadOnlyList<string> targets, int second, string? origin = null) => new(
                Guid.NewGuid(),
                "recognition-snapshot",
                action,
                targets,
                now.AddSeconds(second),
                origin ?? StashReviewCommandProjection.WorkspaceOrigin);
            var actions = new[]
            {
                New(StashReviewActionKind.Pin, ["stash@1:1"], 1),
                New(StashReviewActionKind.Ignore, ["stash@2:2"], 2),
                New(StashReviewActionKind.Rescan, ["stash/case"], 3),
                New(
                    StashReviewActionKind.MergeEntries,
                    [current.ToString("D"), previous.ToString("D")],
                    4,
                    StashReviewCommandProjection.SnapshotMergeOrigin),
            };
            var store = new SqliteStashReviewCommandStore(factory);
            foreach (var action in actions)
            {
                await store.AppendAsync(action, CancellationToken.None);
                await store.AppendAsync(
                    StashReviewCommandProjection.Undo(action, action.CreatedUtc.AddMilliseconds(1)),
                    CancellationToken.None);
            }

            var replayed = await new SqliteStashReviewCommandStore(factory)
                .ListAsync("recognition-snapshot", CancellationToken.None);

            Assert.Equal(8, replayed.Count);
            var state = StashReviewCommandProjection.Project(replayed);
            Assert.Empty(state.PinnedItemKeys);
            Assert.Empty(state.IgnoredItemKeys);
            Assert.Empty(state.RescanContainerPaths);
            Assert.Empty(state.MergedSnapshotIds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
        }
    }
}
