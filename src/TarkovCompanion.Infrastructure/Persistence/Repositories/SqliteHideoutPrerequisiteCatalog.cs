using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the station, trader and skill rows of <c>hideout_requirements</c>, plus the construction
/// time retained in every level's source payload. Read on demand and not cached.
/// </summary>
public sealed class SqliteHideoutPrerequisiteCatalog(SqliteConnectionFactory connectionFactory) : IHideoutPrerequisiteCatalog
{
    public async Task<HideoutPrerequisites> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'hideout_requirements' LIMIT 1;";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return HideoutPrerequisites.None;
        }

        command.CommandText = """
            SELECT requirement.station_id, requirement.level, requirement.requirement_type,
                   requirement.metadata_json,
                   (SELECT name FROM traders WHERE id = json_extract(requirement.metadata_json, '$.trader'))
            FROM hideout_requirements AS requirement
            WHERE requirement.requirement_type IN ('station', 'trader', 'skill')
              AND requirement.metadata_json IS NOT NULL
            ORDER BY requirement.station_id, requirement.level, requirement.rowid;
            """;
        var stations = new List<HideoutStationPrerequisite>();
        var others = new List<HideoutOtherPrerequisite>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var stationId = reader.GetString(0);
                var level = reader.GetInt32(1);
                JsonElement metadata;
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(3));
                    metadata = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (metadata.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (reader.GetString(2))
                {
                    case "station" when Text(metadata, "station") is { } required && Number(metadata, "level") is { } requiredLevel:
                        stations.Add(new(stationId, level, required, requiredLevel));
                        break;
                    case "skill" when Text(metadata, "skill") is { } skill && Number(metadata, "level") is { } skillLevel:
                        others.Add(new(stationId, level, string.Create(CultureInfo.InvariantCulture, $"{skill} level {skillLevel}")));
                        break;
                    case "trader" when (Number(metadata, "value") ?? Number(metadata, "level")) is { } loyalty:
                        var trader = reader.IsDBNull(4) ? "Trader" : reader.GetString(4);
                        others.Add(new(stationId, level, string.Create(CultureInfo.InvariantCulture, $"{trader} loyalty {loyalty}")));
                        break;
                }
            }
        }

        command.CommandText = "SELECT station_id, level, source_json FROM hideout_levels ORDER BY station_id, level;";
        var constructionTimes = new List<HideoutConstructionTime>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(2));
                    if (Number(document.RootElement, "constructionTime") is { } seconds && seconds >= 0)
                    {
                        constructionTimes.Add(new(reader.GetString(0), reader.GetInt32(1), TimeSpan.FromSeconds(seconds)));
                    }
                }
                catch (JsonException)
                {
                    // External catalog JSON is untrusted. One malformed level must not hide valid gates.
                }
            }
        }

        return new(stations, others) { ConstructionTimes = constructionTimes };
    }

    private static string? Text(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static int? Number(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;
}
