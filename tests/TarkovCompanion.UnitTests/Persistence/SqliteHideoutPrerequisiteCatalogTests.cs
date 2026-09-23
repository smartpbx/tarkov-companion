using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests.Persistence;

public sealed class SqliteHideoutPrerequisiteCatalogTests
{
    [Fact]
    public async Task Reads_construction_time_and_skill_and_trader_gates_from_the_catalog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hideout-prerequisites-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO hideout_stations(id, name, source_json)
                    VALUES ('nutrition', 'Nutrition Unit', '{}');
                    INSERT INTO hideout_levels(station_id, level, source_json)
                    VALUES ('nutrition', 1, '{"constructionTime":5400}');
                    INSERT INTO traders(id, name, source_json)
                    VALUES ('mechanic', 'Mechanic', '{}');
                    INSERT INTO hideout_requirements(station_id, level, requirement_type, metadata_json)
                    VALUES
                        ('nutrition', 1, 'skill', '{"skill":"Metabolism","level":3}'),
                        ('nutrition', 1, 'trader', '{"trader":"mechanic","value":2}');
                    """;
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var result = await new SqliteHideoutPrerequisiteCatalog(factory).GetAsync(CancellationToken.None);

            var construction = Assert.Single(result.ConstructionTimes);
            Assert.Equal(("nutrition", 1, TimeSpan.FromMinutes(90)),
                (construction.StationId, construction.TargetLevel, construction.Duration));
            Assert.Equal(
                ["Metabolism level 3", "Mechanic loyalty 2"],
                result.Others.Select(requirement => requirement.Label));
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
