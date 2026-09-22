using Microsoft.Data.Sqlite;
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
}
