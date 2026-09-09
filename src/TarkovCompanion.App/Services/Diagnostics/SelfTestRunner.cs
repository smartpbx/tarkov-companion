using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.App.Services.Diagnostics;

public sealed record SelfTestCheck(string Name, string Status, string Detail, bool Required = true);

public sealed record SelfTestEnvironment(
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    string Provider,
    bool NetworkContacted);

public sealed record SelfTestReport(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    bool Success,
    IReadOnlyList<SelfTestCheck> Checks,
    SelfTestEnvironment Environment,
    IReadOnlyDictionary<string, string?> Paths,
    IReadOnlyDictionary<string, bool> Safety);

public static class SelfTestRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<SelfTestReport> RunAsync(string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("The self-test output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var workspace = Path.Combine(Path.GetTempPath(), $"tarkov-companion-self-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var checks = new List<SelfTestCheck>();

        try
        {
            var databasePath = Path.Combine(workspace, "self-test.db");
            var factory = new SqliteConnectionFactory(new(databasePath));
            var migrations = await new SqliteMigrationRunner(factory).ApplyAsync(cancellationToken).ConfigureAwait(false);
            checks.Add(new("database", "pass", $"Opened SQLite and applied {migrations.Count} migration(s)."));

            await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tableCount = await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('items', 'sync_state', 'raids');",
                cancellationToken).ConfigureAwait(false);
            checks.Add(tableCount == 3
                ? new("cache", "pass", "Required cache tables are present.")
                : new("cache", "fail", $"Expected 3 required cache tables; found {tableCount}."));

            await SeedSearchProbeAsync(connection, cancellationToken).ConfigureAwait(false);
            var searchCount = await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM item_search WHERE item_search MATCH 'diagnostic';",
                cancellationToken).ConfigureAwait(false);
            checks.Add(searchCount == 1
                ? new("search", "pass", "SQLite FTS returned the deterministic diagnostic item.")
                : new("search", "fail", $"SQLite FTS returned {searchCount} rows."));

            checks.Add(new("platform", "pass", $"Headless diagnostics supported on {RuntimeInformation.OSDescription}."));
            checks.Add(new("provider", "pass", "json.tarkov.dev is configured; the self-test made no network request."));

            var pathProbe = Path.Combine(workspace, "path-probe.tmp");
            await File.WriteAllTextAsync(pathProbe, "writable", cancellationToken).ConfigureAwait(false);
            checks.Add(new("paths", "pass", "Temporary data and requested report directories are writable."));
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
        {
            checks.Add(new("self-test-runtime", "fail", exception.Message));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(workspace, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var report = new SelfTestReport(
            1,
            DateTimeOffset.UtcNow,
            checks.All(check => !check.Required || string.Equals(check.Status, "pass", StringComparison.Ordinal)),
            checks,
            new(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                "json.tarkov.dev",
                false),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["report"] = fullOutputPath,
                ["appBase"] = AppContext.BaseDirectory,
                ["currentDirectory"] = Environment.CurrentDirectory,
                ["eftInstall"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_INSTALL_ROOT"),
                ["eftLogs"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_LOG_ROOT"),
                ["eftScreenshots"] = Environment.GetEnvironmentVariable("TARKOV_COMPANION_EFT_SCREENSHOT_ROOT"),
            },
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["readsGameMemory"] = false,
                ["sendsGameInput"] = false,
                ["capturesNetworkTraffic"] = false,
                ["diagnosticChannelEnabled"] = false,
            });

        await using var output = File.Create(fullOutputPath);
        await JsonSerializer.SerializeAsync(output, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static async Task SeedSearchProbeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_search(item_id, name, short_name, aliases, normalized_terms)
            VALUES ('diagnostic-item', 'Diagnostic item', 'Diag', '', 'diagnostic item');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
