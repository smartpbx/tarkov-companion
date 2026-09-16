using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests.DataV2;

internal sealed class V2TestDatabase : IAsyncDisposable
{
    private V2TestDatabase(string path, SqliteConnectionFactory factory)
    {
        Path = path;
        Factory = factory;
    }

    public string Path { get; }
    public SqliteConnectionFactory Factory { get; }

    public static async Task<V2TestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tarkov-companion-v2-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(new(path));
        await new SqliteMigrationRunner(factory).ApplyAsync(cancellationToken);
        return new(path, factory);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(System.IO.Path.GetDirectoryName(Path)!, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
        await ValueTask.CompletedTask;
    }

    public static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
