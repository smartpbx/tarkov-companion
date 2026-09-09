using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteDataRefreshRepository(SqliteConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task RefreshItemsAsync(
        TarkovDevItemsData data,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        ValidateItems(data);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            "CREATE TEMP TABLE IF NOT EXISTS refreshed_item_ids (id TEXT PRIMARY KEY); DELETE FROM refreshed_item_ids;",
            cancellationToken).ConfigureAwait(false);

        foreach (var item in data.Items.Values)
        {
            var updatedUtc = item.Updated ?? observedUtc;
            var propertiesType = GetString(item.Properties, "propertiesType");
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO items(
                    id, name, short_name, normalized_name, normalized_short_name, description,
                    category_type, width, height, slots, base_price, avg_24h_price, last_low_price, low_24h_price, high_24h_price,
                    flea_eligible, icon_url, image_url, wiki_url, properties_type, properties_json,
                    source_updated_utc, raw_json)
                VALUES (
                    $id, $name, $shortName, $normalizedName, $normalizedShortName, $description,
                    $categoryType, $width, $height, $slots, $basePrice, $averagePrice, $lastLowPrice, $lowPrice, $highPrice,
                    $fleaEligible, $iconUrl, $imageUrl, $wikiUrl, $propertiesType, $propertiesJson,
                    $sourceUpdatedUtc, $rawJson)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    short_name = excluded.short_name,
                    normalized_name = excluded.normalized_name,
                    normalized_short_name = excluded.normalized_short_name,
                    description = excluded.description,
                    category_type = excluded.category_type,
                    width = excluded.width,
                    height = excluded.height,
                    slots = excluded.slots,
                    base_price = excluded.base_price,
                    avg_24h_price = excluded.avg_24h_price,
                    last_low_price = excluded.last_low_price,
                    low_24h_price = excluded.low_24h_price,
                    high_24h_price = excluded.high_24h_price,
                    flea_eligible = excluded.flea_eligible,
                    icon_url = excluded.icon_url,
                    image_url = excluded.image_url,
                    wiki_url = excluded.wiki_url,
                    properties_type = excluded.properties_type,
                    properties_json = excluded.properties_json,
                    source_updated_utc = excluded.source_updated_utc,
                    raw_json = excluded.raw_json;
                """,
                cancellationToken,
                ("$id", item.Id),
                ("$name", item.Name),
                ("$shortName", item.ShortName),
                ("$normalizedName", TextNormalizer.Normalize(item.Name)),
                ("$normalizedShortName", TextNormalizer.Normalize(item.ShortName)),
                ("$description", item.Description),
                ("$categoryType", MapCategory(item.Types).ToString()),
                ("$width", item.Width),
                ("$height", item.Height),
                ("$slots", checked(item.Width * item.Height)),
                ("$basePrice", item.BasePrice),
                ("$averagePrice", item.Avg24hPrice),
                ("$lastLowPrice", item.LastLowPrice),
                ("$lowPrice", item.Low24hPrice),
                ("$highPrice", item.High24hPrice),
                ("$fleaEligible", !item.Types.Contains("noFlea", StringComparer.OrdinalIgnoreCase)),
                ("$iconUrl", item.IconLink),
                ("$imageUrl", item.GridImageLink),
                ("$wikiUrl", item.WikiLink),
                ("$propertiesType", propertiesType),
                ("$propertiesJson", item.Properties?.GetRawText()),
                ("$sourceUpdatedUtc", FormatTimestamp(updatedUtc)),
                ("$rawJson", JsonSerializer.Serialize(item, SerializerOptions))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO refreshed_item_ids(id) VALUES ($id);",
                cancellationToken,
                ("$id", item.Id)).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM items WHERE id NOT IN (SELECT id FROM refreshed_item_ids);",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM item_search; DELETE FROM item_sell_offers; DELETE FROM item_category_membership; DELETE FROM item_categories;",
            cancellationToken).ConfigureAwait(false);

        foreach (var category in data.ItemCategories.Values)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO item_categories(id, name) VALUES ($id, $name);",
                cancellationToken,
                ("$id", category.Id),
                ("$name", category.Name)).ConfigureAwait(false);
        }

        foreach (var item in data.Items.Values)
        {
            foreach (var categoryId in item.Categories.Where(data.ItemCategories.ContainsKey).Distinct(StringComparer.Ordinal))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO item_category_membership(item_id, category_id) VALUES ($itemId, $categoryId);",
                    cancellationToken,
                    ("$itemId", item.Id),
                    ("$categoryId", categoryId)).ConfigureAwait(false);
            }

            foreach (var offer in item.SellToTrader)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO item_sell_offers(item_id, vendor_id, vendor_name, value, currency, requirements_json, updated_utc)
                    VALUES ($itemId, $vendorId, $vendorName, $value, $currency, NULL, $updatedUtc);
                    """,
                    cancellationToken,
                    ("$itemId", item.Id),
                    ("$vendorId", offer.Trader),
                    ("$vendorName", offer.Trader),
                    ("$value", offer.PriceRub == 0 ? offer.Price : offer.PriceRub),
                    ("$currency", offer.Currency),
                    ("$updatedUtc", FormatTimestamp(item.Updated ?? observedUtc))).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO item_search(item_id, name, short_name, aliases, normalized_terms) VALUES ($id, $name, $shortName, '', $terms);",
                cancellationToken,
                ("$id", item.Id),
                ("$name", item.Name),
                ("$shortName", item.ShortName),
                ("$terms", $"{TextNormalizer.Normalize(item.Name)} {TextNormalizer.Normalize(item.ShortName)}")).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
                VALUES ($itemId, $timestampUtc, $fleaPrice, $traderValue, 'json.tarkov.dev/items');
                """,
                cancellationToken,
                ("$itemId", item.Id),
                ("$timestampUtc", FormatTimestamp(item.Updated ?? observedUtc)),
                ("$fleaPrice", item.LastLowPrice),
                ("$traderValue", BestTraderValue(item.SellToTrader))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshMapsAsync(
        TarkovDevMapsData data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM maps;", cancellationToken).ConfigureAwait(false);
                foreach (var map in data.Maps.Values)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
                        VALUES ($id, $name, $normalizedName, $duration, NULL, $sourceJson);
                        """,
                        cancellationToken,
                        ("$id", map.Id),
                        ("$name", map.Name),
                        ("$normalizedName", TextNormalizer.Normalize(map.Name)),
                        ("$duration", map.RaidDuration is null ? null : checked(map.RaidDuration * 60)),
                        ("$sourceJson", JsonSerializer.Serialize(map, SerializerOptions))).ConfigureAwait(false);

                    for (var index = 0; index < map.Spawns.Count; index++)
                    {
                        var spawn = map.Spawns[index];
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO map_spawns(id, map_id, type, x, y, z, source_json)
                            VALUES ($id, $mapId, $type, $x, $y, $z, $sourceJson);
                            """,
                            cancellationToken,
                            ("$id", $"{map.Id}:spawn:{index}"),
                            ("$mapId", map.Id),
                            ("$type", string.Join(',', spawn.Categories.Concat(spawn.Sides).Distinct(StringComparer.Ordinal))),
                            ("$x", spawn.Position.X),
                            ("$y", spawn.Position.Y),
                            ("$z", spawn.Position.Z),
                            ("$sourceJson", JsonSerializer.Serialize(spawn, SerializerOptions))).ConfigureAwait(false);
                    }

                    foreach (var extract in map.Extracts)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO map_extracts(id, map_id, name, x, y, z, conditions, source_json)
                            VALUES ($id, $mapId, $name, $x, $y, $z, $conditions, $sourceJson);
                            """,
                            cancellationToken,
                            ("$id", $"{map.Id}:{extract.Id}"),
                            ("$mapId", map.Id),
                            ("$name", extract.Name),
                            ("$x", extract.Position?.X),
                            ("$y", extract.Position?.Y),
                            ("$z", extract.Position?.Z),
                            ("$conditions", JsonSerializer.Serialize(extract.AdditionalData, SerializerOptions)),
                            ("$sourceJson", JsonSerializer.Serialize(extract, SerializerOptions))).ConfigureAwait(false);
                    }

                    foreach (var transit in map.Transits)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO map_transits(id, map_id, source_json) VALUES ($id, $mapId, $sourceJson);",
                            cancellationToken,
                            ("$id", $"{map.Id}:{transit.Id}"),
                            ("$mapId", map.Id),
                            ("$sourceJson", JsonSerializer.Serialize(transit, SerializerOptions))).ConfigureAwait(false);
                    }

                    foreach (var mapLock in map.Locks)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO map_locks(id, map_id, key_item_id, source_json) VALUES ($id, $mapId, $keyItemId, $sourceJson);",
                            cancellationToken,
                            ("$id", $"{map.Id}:{mapLock.Id}"),
                            ("$mapId", map.Id),
                            ("$keyItemId", mapLock.Key),
                            ("$sourceJson", JsonSerializer.Serialize(mapLock, SerializerOptions))).ConfigureAwait(false);
                    }

                    for (var index = 0; index < map.Hazards.Count; index++)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO map_hazards(id, map_id, source_json) VALUES ($id, $mapId, $sourceJson);",
                            cancellationToken,
                            ("$id", $"{map.Id}:hazard:{index}"),
                            ("$mapId", map.Id),
                            ("$sourceJson", map.Hazards[index].GetRawText())).ConfigureAwait(false);
                    }

                    var loot = map.LootContainers.Concat(map.LootLoose).ToArray();
                    for (var index = 0; index < loot.Length; index++)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO map_loot_positions(id, map_id, source_json) VALUES ($id, $mapId, $sourceJson);",
                            cancellationToken,
                            ("$id", $"{map.Id}:loot:{index}"),
                            ("$mapId", map.Id),
                            ("$sourceJson", JsonSerializer.Serialize(loot[index], SerializerOptions))).ConfigureAwait(false);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshTasksAsync(
        TarkovDevTasksData data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM task_objective_items; DELETE FROM task_objectives; DELETE FROM tasks;",
                    cancellationToken).ConfigureAwait(false);
                foreach (var task in data.Tasks.Values)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO tasks(id, name, trader_id, min_level, map_id, source_json)
                        VALUES ($id, $name, $traderId, $minLevel, $mapId, $sourceJson);
                        """,
                        cancellationToken,
                        ("$id", task.Id),
                        ("$name", task.Name),
                        ("$traderId", task.Trader),
                        ("$minLevel", task.MinPlayerLevel),
                        ("$mapId", task.Map),
                        ("$sourceJson", JsonSerializer.Serialize(task, SerializerOptions))).ConfigureAwait(false);
                    foreach (var objective in task.Objectives)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO task_objectives(id, task_id, type, description, map_id, zone_json)
                            VALUES ($id, $taskId, $type, $description, $mapId, $zoneJson);
                            """,
                            cancellationToken,
                            ("$id", objective.Id),
                            ("$taskId", task.Id),
                            ("$type", objective.Type),
                            ("$description", objective.Description),
                            ("$mapId", objective.Maps.FirstOrDefault()),
                            ("$zoneJson", objective.Zones?.GetRawText())).ConfigureAwait(false);
                        foreach (var itemId in objective.Items.Distinct(StringComparer.Ordinal))
                        {
                            await ExecuteAsync(
                                connection,
                                transaction,
                                """
                                INSERT INTO task_objective_items(objective_id, item_id, count, found_in_raid_required)
                                VALUES ($objectiveId, $itemId, $count, $foundInRaid);
                                """,
                                cancellationToken,
                                ("$objectiveId", objective.Id),
                                ("$itemId", itemId),
                                ("$count", objective.Count ?? 1),
                                ("$foundInRaid", objective.FoundInRaid == true)).ConfigureAwait(false);
                        }
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshHideoutAsync(
        IReadOnlyDictionary<string, TarkovDevHideoutStation> data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM hideout_requirements; DELETE FROM hideout_levels; DELETE FROM hideout_stations;",
                    cancellationToken).ConfigureAwait(false);
                foreach (var station in data.Values)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO hideout_stations(id, name, source_json) VALUES ($id, $name, $sourceJson);",
                        cancellationToken,
                        ("$id", station.Id),
                        ("$name", station.Name),
                        ("$sourceJson", JsonSerializer.Serialize(station, SerializerOptions))).ConfigureAwait(false);
                    foreach (var level in station.Levels)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO hideout_levels(station_id, level, source_json) VALUES ($stationId, $level, $sourceJson);",
                            cancellationToken,
                            ("$stationId", station.Id),
                            ("$level", level.Level),
                            ("$sourceJson", JsonSerializer.Serialize(level, SerializerOptions))).ConfigureAwait(false);
                        foreach (var requirement in level.ItemRequirements)
                        {
                            await InsertHideoutRequirementAsync(
                                connection,
                                transaction,
                                station.Id,
                                level.Level,
                                "item",
                                requirement.Item,
                                requirement.Count,
                                requirement.Attributes?.GetRawText(),
                                cancellationToken).ConfigureAwait(false);
                        }

                        foreach (var requirement in level.StationLevelRequirements)
                        {
                            await InsertHideoutRequirementAsync(
                                connection,
                                transaction,
                                station.Id,
                                level.Level,
                                "station",
                                null,
                                requirement.Level,
                                JsonSerializer.Serialize(requirement, SerializerOptions),
                                cancellationToken).ConfigureAwait(false);
                        }

                        foreach (var requirement in level.TraderRequirements)
                        {
                            await InsertHideoutRequirementAsync(
                                connection,
                                transaction,
                                station.Id,
                                level.Level,
                                "trader",
                                null,
                                requirement.Level,
                                JsonSerializer.Serialize(requirement, SerializerOptions),
                                cancellationToken).ConfigureAwait(false);
                        }

                        foreach (var requirement in level.SkillRequirements)
                        {
                            await InsertHideoutRequirementAsync(
                                connection,
                                transaction,
                                station.Id,
                                level.Level,
                                "skill",
                                null,
                                requirement.Level,
                                JsonSerializer.Serialize(requirement, SerializerOptions),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshTradersAsync(
        IReadOnlyDictionary<string, TarkovDevTrader> data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM trader_offers; DELETE FROM trader_levels; DELETE FROM traders;",
                    cancellationToken).ConfigureAwait(false);
                foreach (var trader in data.Values)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO traders(id, name, source_json) VALUES ($id, $name, $sourceJson);",
                        cancellationToken,
                        ("$id", trader.Id),
                        ("$name", trader.Name),
                        ("$sourceJson", JsonSerializer.Serialize(trader, SerializerOptions))).ConfigureAwait(false);
                    foreach (var level in trader.Levels)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO trader_levels(trader_id, level, source_json) VALUES ($traderId, $level, $sourceJson);",
                            cancellationToken,
                            ("$traderId", trader.Id),
                            ("$level", level.Level),
                            ("$sourceJson", JsonSerializer.Serialize(level, SerializerOptions))).ConfigureAwait(false);
                    }
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE item_sell_offers
                    SET vendor_name = COALESCE(
                        (SELECT name FROM traders WHERE traders.id = item_sell_offers.vendor_id),
                        vendor_name);
                    """,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshCraftsAsync(
        IReadOnlyList<TarkovDevCraft> data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM craft_requirements; DELETE FROM craft_outputs; DELETE FROM crafts;",
                    cancellationToken).ConfigureAwait(false);
                foreach (var craft in data)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO crafts(id, station_id, level, source_json) VALUES ($id, $stationId, $level, $sourceJson);",
                        cancellationToken,
                        ("$id", craft.Id),
                        ("$stationId", craft.Station),
                        ("$level", craft.Level),
                        ("$sourceJson", JsonSerializer.Serialize(craft, SerializerOptions))).ConfigureAwait(false);
                    foreach (var requirement in craft.RequiredItems)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO craft_requirements(craft_id, item_id, count, source_json) VALUES ($craftId, $itemId, $count, $sourceJson);",
                            cancellationToken,
                            ("$craftId", craft.Id),
                            ("$itemId", requirement.Item),
                            ("$count", requirement.Count),
                            ("$sourceJson", JsonSerializer.Serialize(requirement, SerializerOptions))).ConfigureAwait(false);
                    }

                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO craft_outputs(craft_id, item_id, count, source_json) VALUES ($craftId, $itemId, $count, $sourceJson);",
                        cancellationToken,
                        ("$craftId", craft.Id),
                        ("$itemId", craft.ProductItem.Item),
                        ("$count", craft.ProductItem.Count),
                        ("$sourceJson", JsonSerializer.Serialize(craft.ProductItem, SerializerOptions))).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshBartersAsync(
        IReadOnlyList<TarkovDevBarter> data,
        CancellationToken cancellationToken)
    {
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM barter_requirements; DELETE FROM barter_outputs; DELETE FROM barters;",
                    cancellationToken).ConfigureAwait(false);
                foreach (var barter in data)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO barters(id, trader_id, source_json) VALUES ($id, $traderId, $sourceJson);",
                        cancellationToken,
                        ("$id", barter.Id),
                        ("$traderId", barter.Trader),
                        ("$sourceJson", JsonSerializer.Serialize(barter, SerializerOptions))).ConfigureAwait(false);
                    foreach (var requirement in barter.RequiredItems)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO barter_requirements(barter_id, item_id, count, source_json) VALUES ($barterId, $itemId, $count, $sourceJson);",
                            cancellationToken,
                            ("$barterId", barter.Id),
                            ("$itemId", requirement.Item),
                            ("$count", requirement.Count),
                            ("$sourceJson", JsonSerializer.Serialize(requirement, SerializerOptions))).ConfigureAwait(false);
                    }

                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO barter_outputs(barter_id, item_id, count, source_json) VALUES ($barterId, $itemId, $count, $sourceJson);",
                        cancellationToken,
                        ("$barterId", barter.Id),
                        ("$itemId", barter.OfferedItem.Item),
                        ("$count", barter.OfferedItem.Count),
                        ("$sourceJson", JsonSerializer.Serialize(barter.OfferedItem, SerializerOptions))).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshPriceHistoryAsync(
        string itemId,
        IReadOnlyList<TarkovDevPricePoint> points,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                foreach (var point in points)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT OR REPLACE INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
                        VALUES ($itemId, $timestampUtc, $fleaPrice, NULL, 'json.tarkov.dev/prices');
                        """,
                        cancellationToken,
                        ("$itemId", itemId),
                        ("$timestampUtc", FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp))),
                        ("$fleaPrice", point.Price ?? point.PriceMin)).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await action(connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> InsertHideoutRequirementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stationId,
        int level,
        string type,
        string? itemId,
        decimal count,
        string? metadataJson,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO hideout_requirements(station_id, level, requirement_type, item_id, count, metadata_json)
            VALUES ($stationId, $level, $type, $itemId, $count, $metadataJson);
            """,
            cancellationToken,
            ("$stationId", stationId),
            ("$level", level),
            ("$type", type),
            ("$itemId", itemId),
            ("$count", count),
            ("$metadataJson", metadataJson));

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateItems(TarkovDevItemsData data)
    {
        foreach (var pair in data.Items)
        {
            var item = pair.Value;
            if (!string.Equals(pair.Key, item.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Item dictionary key '{pair.Key}' does not match item id '{item.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.ShortName) || item.Width <= 0 || item.Height <= 0)
            {
                throw new InvalidDataException($"Item '{pair.Key}' is missing required normalized persistence fields.");
            }
        }
    }

    private static string? GetString(JsonElement? element, string propertyName) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static ItemCategory MapCategory(IReadOnlyList<string> types)
    {
        foreach (var type in types)
        {
            var category = type.ToLowerInvariant() switch
            {
                "ammo" => ItemCategory.Ammunition,
                "ammobox" => ItemCategory.AmmunitionPack,
                "keys" => ItemCategory.Key,
                "provisions" => ItemCategory.Provision,
                "meds" or "injectors" => ItemCategory.Medicine,
                "gun" => ItemCategory.Weapon,
                "mods" => ItemCategory.Attachment,
                "armor" => ItemCategory.Armor,
                "armorplate" => ItemCategory.Plate,
                "helmet" => ItemCategory.Helmet,
                "headphones" => ItemCategory.Headset,
                "rig" => ItemCategory.Rig,
                "backpack" => ItemCategory.Backpack,
                "container" => ItemCategory.Container,
                "barter" => ItemCategory.Barter,
                _ => ItemCategory.Unknown,
            };
            if (category != ItemCategory.Unknown)
            {
                return category;
            }
        }

        return ItemCategory.Unknown;
    }

    private static long? BestTraderValue(IReadOnlyList<TarkovDevTraderPrice> offers) =>
        offers.Count == 0
            ? null
            : offers.Max(offer => offer.PriceRub == 0 ? offer.Price : offer.PriceRub);
}
