using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteRaidHistoryService(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IRaidHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raid);
        if (raid.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(raid.Mode))
        {
            throw new ArgumentException("Raid history requires a profile id and mode.", nameof(raid));
        }

        var raidId = raid.Id == Guid.Empty ? Guid.NewGuid() : raid.Id;
        var now = _timeProvider.GetUtcNow();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = (SqliteTransaction)transaction;
            profile.CommandText = """
                INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
                VALUES ($id, 'Local profile', $mode, 'Unknown', 1, $now, $now)
                ON CONFLICT(id) DO NOTHING;
                """;
            profile.Parameters.AddWithValue("$id", raid.ProfileId.ToString("D"));
            profile.Parameters.AddWithValue("$mode", raid.Mode);
            profile.Parameters.AddWithValue("$now", Format(now));
            await profile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO raids(id, profile_id, map_id, mode, start_utc, end_utc, outcome, notes)
                VALUES ($id, $profileId, $mapId, $mode, $startUtc, $endUtc, $outcome, $notes);
                """;
            BindRaid(command, raid with { Id = raidId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return raidId;
    }

    public async Task RecordEventAsync(
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var payload = JsonDocument.Parse(payloadJson);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raid_events(raid_id, timestamp_utc, type, payload_json)
            VALUES ($raidId, $timestampUtc, $type, $payloadJson);
            """;
        command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
        command.Parameters.AddWithValue("$timestampUtc", Format(timestampUtc));
        command.Parameters.AddWithValue("$type", type.Trim());
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one raid's path back out of the events it recorded while it was running.
    /// </summary>
    /// <remarks>
    /// Stored as ordinary raid events with the type "position", not in the raid_positions
    /// table beside them, which has been empty since the schema was written. The events are
    /// where the data actually is, so that is where this reads it from; filling a second table
    /// to say the same thing twice would be work in the service of tidiness.
    ///
    /// A payload that will not parse is skipped rather than failing the read. One unreadable
    /// screenshot costs one point on a trail, and a trail missing a point is still a trail.
    /// </remarks>
    public async Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
        Guid raidId,
        CancellationToken cancellationToken)
    {
        var positions = new List<ScreenshotPosition>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM raid_events
            WHERE raid_id = $raidId AND type = 'position'
            ORDER BY timestamp_utc, id;
            """;
        command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            try
            {
                // A payload that is valid JSON but is not one of these deserialises to a
                // record of defaults rather than throwing: no filename, a zero timestamp and
                // a position at the origin. Drawn on a map that is a point somebody never
                // stood on, which is the failure this codebase keeps having to refuse. Every
                // real one came from a screenshot and therefore has its name.
                if (JsonSerializer.Deserialize<ScreenshotPosition>(reader.GetString(0), JsonOptions)
                    is { Filename.Length: > 0 } position)
                {
                    positions.Add(position);
                }
            }
            catch (JsonException)
            {
                // One unreadable screenshot costs one point on a trail.
            }
        }

        return positions;
    }

    public async Task EndAsync(
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE raids
            SET end_utc = $endUtc, outcome = $outcome, notes = $notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", raidId.ToString("D"));
        command.Parameters.AddWithValue("$endUtc", Format(endUtc));
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Raid '{raidId:D}' does not exist.");
        }
    }

    public async Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, map_id, mode, start_utc, end_utc, outcome, notes
            FROM raids
            ORDER BY COALESCE(start_utc, end_utc) DESC, id;
            """;
        var entries = new List<RaidHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadRaid(reader));
        }

        return entries;
    }

    public async Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var raids = await ListAsync(cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("id,profile_id,map_id,mode,start_utc,end_utc,outcome,notes".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        foreach (var raid in raids)
        {
            var row = string.Join(',', new[]
            {
                Escape(raid.Id.ToString("D")),
                Escape(raid.ProfileId.ToString("D")),
                Escape(raid.MapId),
                Escape(raid.Mode),
                Escape(raid.StartedUtc is null ? null : Format(raid.StartedUtc.Value)),
                Escape(raid.EndedUtc is null ? null : Format(raid.EndedUtc.Value)),
                Escape(raid.Outcome),
                Escape(raid.Notes),
            });
            await writer.WriteLineAsync(row.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var raids = await ListAsync(cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(destination, raids, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void BindRaid(SqliteCommand command, RaidHistoryEntry raid)
    {
        command.Parameters.AddWithValue("$id", raid.Id.ToString("D"));
        command.Parameters.AddWithValue("$profileId", raid.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$mapId", (object?)raid.MapId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", raid.Mode);
        command.Parameters.AddWithValue("$startUtc", raid.StartedUtc is null ? DBNull.Value : Format(raid.StartedUtc.Value));
        command.Parameters.AddWithValue("$endUtc", raid.EndedUtc is null ? DBNull.Value : Format(raid.EndedUtc.Value));
        command.Parameters.AddWithValue("$outcome", (object?)raid.Outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)raid.Notes ?? DBNull.Value);
    }

    private static RaidHistoryEntry ReadRaid(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        ParseNullable(reader, 4),
        ParseNullable(reader, 5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static DateTimeOffset? ParseNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
