using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Persists the bounded screenshot-retention preference without storing captures.</summary>
public sealed class SqliteScreenshotRetentionStore(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IScreenshotRetentionStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT screenshot_retention_enabled, screenshot_retention_hours
            FROM retention_policies
            WHERE policy_key = 'local';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetBoolean(0), reader.IsDBNull(1) ? ScreenshotRetentionSettings.Default.RetentionHours : reader.GetInt32(1))
            : ScreenshotRetentionSettings.Default;
    }

    public async Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retention_policies(
                policy_key, screenshot_retention_enabled, screenshot_retention_hours,
                debug_capture_enabled, data_retention_days, updated_utc, extension_json)
            VALUES ('local', $enabled, $hours, 0, NULL, $updatedUtc, '{}')
            ON CONFLICT(policy_key) DO UPDATE SET
                screenshot_retention_enabled = excluded.screenshot_retention_enabled,
                screenshot_retention_hours = excluded.screenshot_retention_hours,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$enabled", settings.IsEnabled);
        command.Parameters.AddWithValue("$hours", settings.SafeRetentionHours);
        command.Parameters.AddWithValue("$updatedUtc", _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
