using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Recognition.Learning;

namespace TarkovCompanion.Infrastructure.Persistence.Learning;

/// <summary>What the player taught by correcting a read (0020), kept in the local database only.</summary>
/// <remarks>
/// Bounded so a long career of corrections cannot grow the database without limit: the newest
/// <see cref="MaximumIconsPerItem"/> crops of an item and <see cref="MaximumIcons"/> in all are
/// kept. A crop is a few kilobytes (one item's squares), so the ceiling is a few megabytes.
/// </remarks>
public sealed class SqliteCorrectionMemoryStore(SqliteConnectionFactory connectionFactory) : ICorrectionMemoryStore
{
    public const int MaximumIconsPerItem = 8;
    public const int MaximumIcons = 1000;

    public async Task AddIconAsync(LearnedIconReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.ItemId);
        if (reference.Png.IsEmpty)
        {
            throw new ArgumentException("A learned icon needs its picture.", nameof(reference));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO learned_icon_references(reference_id, item_id, width_cells, height_cells, png, created_utc)
                VALUES ($id, $item, $width, $height, $png, $created);
                DELETE FROM learned_icon_references
                WHERE item_id = $item AND reference_id NOT IN (
                    SELECT reference_id FROM learned_icon_references WHERE item_id = $item
                    ORDER BY created_utc DESC, reference_id DESC LIMIT $perItem);
                DELETE FROM learned_icon_references
                WHERE reference_id NOT IN (
                    SELECT reference_id FROM learned_icon_references
                    ORDER BY created_utc DESC, reference_id DESC LIMIT $total);
                """;
            write.Parameters.AddWithValue("$id", reference.ReferenceId.ToString("D"));
            write.Parameters.AddWithValue("$item", reference.ItemId.Trim());
            write.Parameters.AddWithValue("$width", reference.WidthCells);
            write.Parameters.AddWithValue("$height", reference.HeightCells);
            write.Parameters.AddWithValue("$png", reference.Png.ToArray());
            write.Parameters.AddWithValue("$created", Format(reference.CreatedUtc));
            write.Parameters.AddWithValue("$perItem", MaximumIconsPerItem);
            write.Parameters.AddWithValue("$total", MaximumIcons);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LearnedIconReference>> ListIconsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT reference_id, item_id, width_cells, height_cells, png, created_utc
            FROM learned_icon_references ORDER BY created_utc, reference_id;
            """;
        var references = new List<LearnedIconReference>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            references.Add(new(
                Guid.ParseExact(reader.GetString(0), "D"),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                (byte[])reader.GetValue(4),
                Parse(reader.GetString(5))));
        }

        return references;
    }

    public async Task<LearnedTextAlias> RecordAliasPickAsync(
        string kind,
        string text,
        string itemId,
        string itemName,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        var normalized = LearnedTextAlias.Normalize(text);
        if (normalized.Length is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var write = connection.CreateCommand();
        write.CommandText = """
            INSERT INTO learned_text_aliases(kind, normalized_text, item_id, item_name, picks, updated_utc)
            VALUES ($kind, $text, $item, $name, 1, $utc)
            ON CONFLICT(kind, normalized_text, item_id) DO UPDATE SET
                picks = picks + 1, item_name = excluded.item_name, updated_utc = excluded.updated_utc
            RETURNING picks;
            """;
        write.Parameters.AddWithValue("$kind", kind);
        write.Parameters.AddWithValue("$text", normalized);
        write.Parameters.AddWithValue("$item", itemId.Trim());
        write.Parameters.AddWithValue("$name", itemName.Trim());
        write.Parameters.AddWithValue("$utc", Format(utc));
        var picks = Convert.ToInt32(await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new(kind, normalized, itemId.Trim(), itemName.Trim(), picks, utc.ToUniversalTime());
    }

    public async Task<IReadOnlyList<LearnedTextAlias>> ListActiveAliasesAsync(string kind, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT normalized_text, item_id, item_name, picks, updated_utc
            FROM learned_text_aliases
            WHERE kind = $kind AND picks >= $picks
            ORDER BY normalized_text, item_id;
            """;
        read.Parameters.AddWithValue("$kind", kind);
        read.Parameters.AddWithValue("$picks", LearnedTextAlias.PicksToBecomeAlias);
        var aliases = new List<LearnedTextAlias>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            aliases.Add(new(kind, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), Parse(reader.GetString(4))));
        }

        return aliases;
    }

    public async Task SetFrameCorrectionAsync(
        string frameSha256,
        string targetKey,
        string itemId,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var write = connection.CreateCommand();
        write.CommandText = """
            INSERT INTO learned_frame_corrections(frame_sha256, target_key, item_id, created_utc)
            VALUES ($frame, $target, $item, $utc)
            ON CONFLICT(frame_sha256, target_key) DO UPDATE SET item_id = excluded.item_id, created_utc = excluded.created_utc;
            """;
        write.Parameters.AddWithValue("$frame", frameSha256.Trim());
        write.Parameters.AddWithValue("$target", targetKey.Trim());
        write.Parameters.AddWithValue("$item", itemId.Trim());
        write.Parameters.AddWithValue("$utc", Format(utc));
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListFrameCorrectionsAsync(string frameSha256, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameSha256);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT target_key, item_id FROM learned_frame_corrections WHERE frame_sha256 = $frame;";
        read.Parameters.AddWithValue("$frame", frameSha256.Trim());
        var corrections = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            corrections[reader.GetString(0)] = reader.GetString(1);
        }

        return corrections;
    }

    public async Task<CorrectionMemoryCounts> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM learned_icon_references),
                (SELECT COUNT(*) FROM learned_text_aliases WHERE picks >= $picks),
                (SELECT COUNT(*) FROM learned_frame_corrections);
            """;
        read.Parameters.AddWithValue("$picks", LearnedTextAlias.PicksToBecomeAlias);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
            : CorrectionMemoryCounts.None;
    }

    public async Task ClearAsync(CorrectionMemoryKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var write = connection.CreateCommand();
        write.CommandText = kind switch
        {
            CorrectionMemoryKind.Icons => "DELETE FROM learned_icon_references;",
            CorrectionMemoryKind.Names => "DELETE FROM learned_text_aliases;",
            CorrectionMemoryKind.FrameCorrections => "DELETE FROM learned_frame_corrections;",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
