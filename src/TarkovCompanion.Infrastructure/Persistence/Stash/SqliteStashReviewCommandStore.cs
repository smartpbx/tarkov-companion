using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Infrastructure.Persistence.Stash;

/// <summary>
/// Keeps manual review intent separate from immutable recognition evidence. The snapshot page reads
/// this log afresh when a snapshot opens, so an app restart cannot erase pending corrections.
/// </summary>
public sealed class SqliteStashReviewCommandStore(SqliteConnectionFactory connectionFactory)
    : IStashReviewCommandSink
{
    public async Task AppendAsync(StashReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var write = connection.CreateCommand();
        write.CommandText = """
            INSERT INTO stash_review_commands(
                command_id, snapshot_id, action, target_item_keys_json, created_utc,
                origin_identifier, corrected_item_id, corrected_quantity, reason)
            VALUES (
                $command, $snapshot, $action, $targets, $created,
                $origin, $correctedItem, $correctedQuantity, $reason);
            """;
        write.Parameters.AddWithValue("$command", command.CommandId.ToString("D"));
        write.Parameters.AddWithValue("$snapshot", command.SnapshotId);
        write.Parameters.AddWithValue("$action", (int)command.Action);
        write.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(command.TargetItemKeys));
        write.Parameters.AddWithValue("$created", FormatTimestamp(command.CreatedUtc));
        write.Parameters.AddWithValue("$origin", command.OriginIdentifier);
        write.Parameters.AddWithValue("$correctedItem", (object?)command.CorrectedItemId ?? DBNull.Value);
        write.Parameters.AddWithValue("$correctedQuantity", (object?)command.CorrectedQuantity ?? DBNull.Value);
        write.Parameters.AddWithValue("$reason", (object?)command.Reason ?? DBNull.Value);
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StashReviewCommand>> ListAsync(
        string snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        var normalized = snapshotId.Trim();
        if (normalized.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotId));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT command_id, action, target_item_keys_json, created_utc,
                   origin_identifier, corrected_item_id, corrected_quantity, reason
            FROM stash_review_commands
            WHERE snapshot_id = $snapshot
            ORDER BY created_utc, command_id;
            """;
        read.Parameters.AddWithValue("$snapshot", normalized);
        var commands = new List<StashReviewCommand>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var targets = JsonSerializer.Deserialize<string[]>(reader.GetString(2))
                    ?? throw new InvalidDataException("A persisted stash review target list is null.");
                commands.Add(new StashReviewCommand(
                    Guid.ParseExact(reader.GetString(0), "D"),
                    normalized,
                    (StashReviewActionKind)reader.GetInt32(1),
                    targets,
                    ParseTimestamp(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or InvalidCastException)
            {
                throw new InvalidDataException("A persisted stash review command is invalid.", exception);
            }
        }

        return commands.AsReadOnly();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result) || result == default || result.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A persisted stash review time is not canonical UTC.");
        }

        return result;
    }
}
