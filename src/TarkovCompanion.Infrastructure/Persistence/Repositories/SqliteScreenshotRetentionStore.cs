using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Persists the bounded screenshot-retention preference without storing captures.</summary>
public sealed class SqliteScreenshotRetentionStore(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null,
    string? legacySettingsPath = null) : IScreenshotRetentionStore
{
    private const int MaximumLegacySettingsBytes = 4 * 1024;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken)
    {
        if (await TryReadAsync(cancellationToken).ConfigureAwait(false) is { } current)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryReadAsync(cancellationToken).ConfigureAwait(false) is { } raced)
            {
                return raced;
            }

            // The JSON preference existed before V2 moved retention into SQLite. Import it on
            // the first read, before the retention service can act, so an explicit opt-out cannot
            // silently turn back on after an upgrade. The source file is retained for rollback.
            var imported = await ReadLegacyOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(imported, cancellationToken).ConfigureAwait(false);
            return imported;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScreenshotRetentionSettings?> TryReadAsync(CancellationToken cancellationToken)
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
            : null;
    }

    private async Task SaveCoreAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
    {
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

    private async Task<ScreenshotRetentionSettings> ReadLegacyOrDefaultAsync(CancellationToken cancellationToken)
    {
        if (legacySettingsPath is null)
        {
            return ScreenshotRetentionSettings.Default;
        }

        string? text = null;
        try
        {
            var info = new FileInfo(legacySettingsPath);
            if (!info.Exists)
            {
                await RecordRecoveryAsync("missing", null, "legacy-settings-missing", cancellationToken)
                    .ConfigureAwait(false);
                return ScreenshotRetentionSettings.Default;
            }

            if (info.Length is <= 0 or > MaximumLegacySettingsBytes)
            {
                await RecordRecoveryAsync("malformed", null, "legacy-settings-size", cancellationToken)
                    .ConfigureAwait(false);
                return ScreenshotRetentionSettings.Default;
            }

            text = await File.ReadAllTextAsync(legacySettingsPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<LegacyRetentionDocument>(
                text,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (document is null || document.RetentionHours is < 1 or > 720)
            {
                throw new JsonException("The legacy screenshot retention preference is invalid.");
            }

            await RecordRecoveryAsync("current", Hash(text), "legacy-settings-imported", cancellationToken)
                .ConfigureAwait(false);
            return new(document.Enabled, document.RetentionHours);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            await RecordRecoveryAsync(
                "malformed",
                text is null ? null : Hash(text),
                exception is JsonException ? "legacy-settings-json-invalid" : "legacy-settings-read-failed",
                cancellationToken).ConfigureAwait(false);
            return ScreenshotRetentionSettings.Default;
        }
    }

    private async Task RecordRecoveryAsync(
        string state,
        string? contentHash,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_json_recovery(document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ('settings:screenshot-retention', $state, $detected, $hash, $diagnostic)
            ON CONFLICT(document_key) DO UPDATE SET
                state = excluded.state,
                detected_utc = excluded.detected_utc,
                content_sha256 = excluded.content_sha256,
                diagnostic_code = excluded.diagnostic_code;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$detected", _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$diagnostic", diagnosticCode);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record LegacyRetentionDocument(bool Enabled, int RetentionHours);
}
