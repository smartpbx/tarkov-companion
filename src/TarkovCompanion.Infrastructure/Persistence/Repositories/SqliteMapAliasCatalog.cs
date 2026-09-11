using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the location token the game logs, paired with the map id the companion uses.
/// </summary>
/// <remarks>
/// json.tarkov.dev publishes a map's <c>nameId</c> next to its <c>normalizedName</c>, and
/// <c>nameId</c> is exactly what Escape from Tarkov writes into its own logs. Deriving the
/// pairing from synced data rather than maintaining it by hand matters: the hand-written
/// table this replaces mapped night Factory and Sandbox_high onto the wrong locations, used
/// "labs" where the catalog says "the-lab", and knew nothing of Labyrinth, Icebreaker, the
/// tutorial or the dark Lab. It also gains new maps on the next sync rather than the next
/// release.
/// </remarks>
public sealed class SqliteMapAliasCatalog(SqliteConnectionFactory connectionFactory) : IMapAliasCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _aliases;

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken)
    {
        if (_aliases is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _aliases ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _aliases = null;

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        // The columns hold a display name and a normalized display name, neither of which is
        // the upstream slug, so both halves of the pairing are read out of the stored payload.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_json FROM maps;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var (token, mapId) = ReadPair(reader.GetString(0));
            if (token is not null && mapId is not null)
            {
                aliases[token] = mapId;
            }
        }

        return aliases;
    }

    private static (string? Token, string? MapId) ReadPair(string sourceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadString(root, "nameId"), ReadString(root, "normalizedName"));
        }
        catch (JsonException)
        {
            // One unreadable map must not cost the rest of the table.
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
