using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

internal delegate Task DataRefreshCommitAction(
    SqliteConnection connection,
    SqliteTransaction transaction,
    CancellationToken cancellationToken);

public enum PriceHistoryRefreshOutcome
{
    Updated,
    ItemNotInCatalog,
}

public sealed class SqliteDataRefreshRepository(SqliteConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task RefreshItemsAsync(
        TarkovDevItemsData data,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken) =>
        RefreshItemsWithCommitAsync(data, observedUtc, null, cancellationToken);

    internal async Task RefreshItemsWithCommitAsync(
        TarkovDevItemsData data,
        DateTimeOffset observedUtc,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "items");
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
            var shortName = ResolveShortName(item);
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
                ("$shortName", shortName),
                ("$normalizedName", TextNormalizer.Normalize(item.Name)),
                ("$normalizedShortName", TextNormalizer.Normalize(shortName)),
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
                ("$name", Named(category.Name, category.Id))).ConfigureAwait(false);
        }

        foreach (var item in data.Items.Values)
        {
            var shortName = ResolveShortName(item);
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
                var value = offer.PriceRub is > 0 ? offer.PriceRub : offer.Price;
                if (value is not > 0)
                {
                    // An absent upstream price is unknown, not a zero-valued offer. The full
                    // offer remains in items.raw_json for a later importer that understands it.
                    continue;
                }

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
                    ("$value", value.Value),
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
                ("$shortName", shortName),
                ("$terms", $"{TextNormalizer.Normalize(item.Name)} {TextNormalizer.Normalize(shortName)}")).ConfigureAwait(false);

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

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO item_metrics_v2(
                    item_id, weight_kg, flea_price_roubles, trader_value_roubles, measured_utc, source)
                VALUES ($itemId, $weight, $fleaPrice, $traderValue, $measuredUtc, 'json.tarkov.dev/items')
                ON CONFLICT(item_id) DO UPDATE SET
                    weight_kg = excluded.weight_kg,
                    flea_price_roubles = excluded.flea_price_roubles,
                    trader_value_roubles = excluded.trader_value_roubles,
                    measured_utc = excluded.measured_utc,
                    source = excluded.source;
                """,
                cancellationToken,
                ("$itemId", item.Id),
                ("$weight", item.Weight ?? GetFiniteDouble(item.Properties, "weight")),
                ("$fleaPrice", item.LastLowPrice),
                ("$traderValue", BestTraderValue(item.SellToTrader)),
                ("$measuredUtc", item.Updated is { } measured ? FormatTimestamp(measured) : null)).ConfigureAwait(false);
        }

        // A payload without usable rates leaves the last ones in place. They carry their own
        // date, so a reader refuses them once they are old rather than this erasing them early.
        if (data.FleaMarket is { SellOfferFeeRate: { } offerRate, SellRequirementFeeRate: { } requirementRate } &&
            double.IsFinite(offerRate) && offerRate is >= 0 and <= 1 &&
            double.IsFinite(requirementRate) && requirementRate is >= 0 and <= 1)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO flea_market_settings(id, sell_offer_fee_rate, sell_requirement_fee_rate, observed_utc)
                VALUES (1, $offerRate, $requirementRate, $observedUtc)
                ON CONFLICT(id) DO UPDATE SET
                    sell_offer_fee_rate = excluded.sell_offer_fee_rate,
                    sell_requirement_fee_rate = excluded.sell_requirement_fee_rate,
                    observed_utc = excluded.observed_utc;
                """,
                cancellationToken,
                ("$offerRate", offerRate),
                ("$requirementRate", requirementRate),
                ("$observedUtc", FormatTimestamp(observedUtc))).ConfigureAwait(false);
        }

        if (commitAction is not null)
        {
            await commitAction(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RefreshMapsAsync(
        TarkovDevMapsData data,
        CancellationToken cancellationToken) =>
        RefreshMapsWithCommitAsync(data, null, cancellationToken);

    internal async Task RefreshMapsWithCommitAsync(
        TarkovDevMapsData data,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "maps");
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM maps;", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DELETE FROM loot_containers;", cancellationToken)
                    .ConfigureAwait(false);
                foreach (var container in data.LootContainers.Values)
                {
                    // Only the normalized name. Upstream's "name" is the literal string
                    // "<id> Name" for every container in the feed, so storing it would put an
                    // identifier one careless binding away from the player's map.
                    if (container.NormalizedName is not { Length: > 0 } normalizedName)
                    {
                        continue;
                    }

                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT OR REPLACE INTO loot_containers(id, normalized_name) VALUES ($id, $normalizedName);",
                        cancellationToken,
                        ("$id", container.Id),
                        ("$normalizedName", normalizedName)).ConfigureAwait(false);
                }

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
                        ("$name", Named(map.Name, map.Id)),
                        ("$normalizedName", TextNormalizer.Normalize(map.Name)),
                        ("$duration", map.RaidDuration is null ? null : checked(map.RaidDuration * 60)),
                        ("$sourceJson", JsonSerializer.Serialize(map, SerializerOptions))).ConfigureAwait(false);

                    // One upstream map can list the same extract id twice - the same exit
                    // for two factions, or two positions for one exit. Those are genuinely
                    // different rows, so the ordinal is part of the synthetic key. Without
                    // it the second insert violated the primary key and rolled back every
                    // map, spawn, extract and loot position in the refresh.
                    foreach (var (extract, extractOrdinal) in map.Extracts.Select((value, ordinal) => (value, ordinal)))
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO map_extracts(id, map_id, name, x, y, z, conditions, source_json)
                            VALUES ($id, $mapId, $name, $x, $y, $z, $conditions, $sourceJson);
                            """,
                            cancellationToken,
                            ("$id", CompositeIdentity(map.Id, extractOrdinal.ToString(CultureInfo.InvariantCulture), extract.Id)),
                            ("$mapId", map.Id),
                            ("$name", Named(extract.Name, extract.Id)),
                            ("$x", extract.Position?.X),
                            ("$y", extract.Position?.Y),
                            ("$z", extract.Position?.Z),
                            ("$conditions", JsonSerializer.Serialize(extract.AdditionalData, SerializerOptions)),
                            ("$sourceJson", JsonSerializer.Serialize(extract, SerializerOptions))).ConfigureAwait(false);
                    }

                    foreach (var mapLock in map.Locks)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "INSERT INTO map_locks(id, map_id, key_item_id, source_json) VALUES ($id, $mapId, $keyItemId, $sourceJson);",
                            cancellationToken,
                            ("$id", CompositeIdentity(map.Id, mapLock.Id)),
                            ("$mapId", map.Id),
                            ("$keyItemId", mapLock.Key),
                            ("$sourceJson", JsonSerializer.Serialize(mapLock, SerializerOptions))).ConfigureAwait(false);
                    }

                }
            },
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    public Task RefreshTasksAsync(
        QuestCatalogSnapshot catalog,
        CancellationToken cancellationToken) =>
        RefreshTasksWithCommitAsync(catalog, null, cancellationToken);

    internal async Task RefreshTasksWithCommitAsync(
        QuestCatalogSnapshot catalog,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await InTransactionAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM task_objective_items; DELETE FROM task_objectives; DELETE FROM tasks;",
                    cancellationToken).ConfigureAwait(false);

                var provenance = catalog.Provenance;
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM quest_catalog_tasks
                    WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language;

                    INSERT INTO quest_catalog_snapshots(
                        source_key, source_uri, local_game_mode, source_mode, language,
                        payload_sha256, translated_payload_sha256, etag, last_modified_utc,
                        fetched_utc, validated_utc, raw_json, translated_json)
                    VALUES (
                        $sourceKey, $sourceUri, $localGameMode, $sourceMode, $language,
                        $payloadHash, $translatedPayloadHash, $etag, $lastModifiedUtc,
                        $fetchedUtc, $validatedUtc, $rawJson, $translatedJson)
                    ON CONFLICT(source_key, source_mode, language) DO UPDATE SET
                        source_uri = excluded.source_uri,
                        local_game_mode = excluded.local_game_mode,
                        payload_sha256 = excluded.payload_sha256,
                        translated_payload_sha256 = excluded.translated_payload_sha256,
                        etag = excluded.etag,
                        last_modified_utc = excluded.last_modified_utc,
                        fetched_utc = excluded.fetched_utc,
                        validated_utc = excluded.validated_utc,
                        raw_json = excluded.raw_json,
                        translated_json = excluded.translated_json;
                    """,
                    cancellationToken,
                    ("$sourceKey", provenance.Source),
                    ("$sourceUri", provenance.SourceUri),
                    ("$localGameMode", provenance.GameMode.ToString()),
                    ("$sourceMode", provenance.SourceMode),
                    ("$language", provenance.Language),
                    ("$payloadHash", provenance.PayloadSha256),
                    ("$translatedPayloadHash", provenance.TranslatedPayloadSha256),
                    ("$etag", provenance.ETag),
                    ("$lastModifiedUtc", provenance.LastModifiedUtc is null ? null : FormatTimestamp(provenance.LastModifiedUtc.Value)),
                    ("$fetchedUtc", FormatTimestamp(provenance.FetchedUtc)),
                    ("$validatedUtc", FormatTimestamp(provenance.ValidatedUtc)),
                    ("$rawJson", catalog.RawSourceJson),
                    ("$translatedJson", catalog.TranslatedSourceJson)).ConfigureAwait(false);

                foreach (var task in catalog.Tasks)
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
                        ("$name", Named(task.Name, task.Id)),
                        ("$traderId", task.TraderId),
                        ("$minLevel", task.MinimumPlayerLevel),
                        ("$mapId", task.PrimaryMapId),
                        ("$sourceJson", task.RawSourceJson)).ConfigureAwait(false);
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO quest_catalog_tasks(
                            source_key, source_mode, language, id, name, normalized_name,
                            trader_id, min_player_level, faction_name, primary_map_id,
                            restartable, kappa_required, lightkeeper_required, required_prestige_id,
                            available_delay_seconds_min, available_delay_seconds_max,
                            source_game_modes_json, wiki_url, raw_json)
                        VALUES (
                            $sourceKey, $sourceMode, $language, $id, $name, $normalizedName,
                            $traderId, $minLevel, $factionName, $primaryMapId,
                            $restartable, $kappaRequired, $lightkeeperRequired, $requiredPrestigeId,
                            $delayMin, $delayMax, $sourceGameMode, $wikiUrl, $rawJson);
                        """,
                        cancellationToken,
                        ("$sourceKey", provenance.Source),
                        ("$sourceMode", provenance.SourceMode),
                        ("$language", provenance.Language),
                        ("$id", task.Id),
                        ("$name", Named(task.Name, task.Id)),
                        ("$normalizedName", task.NormalizedName),
                        ("$traderId", task.TraderId),
                        ("$minLevel", task.MinimumPlayerLevel),
                        ("$factionName", task.FactionName),
                        ("$primaryMapId", task.PrimaryMapId),
                        ("$restartable", task.Restartable),
                        ("$kappaRequired", task.KappaRequired),
                        ("$lightkeeperRequired", task.LightkeeperRequired),
                        ("$requiredPrestigeId", task.RequiredPrestigeId),
                        ("$delayMin", task.AvailableDelaySecondsMinimum),
                        ("$delayMax", task.AvailableDelaySecondsMaximum),
                        ("$sourceGameMode", JsonSerializer.Serialize(task.SourceGameModes, SerializerOptions)),
                        ("$wikiUrl", task.WikiUri),
                        ("$rawJson", task.RawSourceJson)).ConfigureAwait(false);

                    foreach (var requirement in task.Requirements)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO quest_task_requirements(
                                source_key, source_mode, language, task_id, source_ordinal,
                                required_task_id, raw_json)
                            VALUES (
                                $sourceKey, $sourceMode, $language, $taskId, $sourceOrdinal,
                                $requiredTaskId, $rawJson);
                            """,
                            cancellationToken,
                            ("$sourceKey", provenance.Source),
                            ("$sourceMode", provenance.SourceMode),
                            ("$language", provenance.Language),
                            ("$taskId", task.Id),
                            ("$sourceOrdinal", requirement.SourceOrdinal),
                            ("$requiredTaskId", requirement.RequiredTaskId),
                            ("$rawJson", requirement.RawSourceJson)).ConfigureAwait(false);
                        for (var statusOrdinal = 0; statusOrdinal < requirement.RequiredStatuses.Count; statusOrdinal++)
                        {
                            await ExecuteAsync(
                                connection,
                                transaction,
                                """
                                INSERT INTO quest_task_requirement_statuses(
                                    source_key, source_mode, language, task_id, requirement_ordinal,
                                    status_ordinal, required_status)
                                VALUES (
                                    $sourceKey, $sourceMode, $language, $taskId, $requirementOrdinal,
                                    $statusOrdinal, $requiredStatus);
                                """,
                                cancellationToken,
                                ("$sourceKey", provenance.Source),
                                ("$sourceMode", provenance.SourceMode),
                                ("$language", provenance.Language),
                                ("$taskId", task.Id),
                                ("$requirementOrdinal", requirement.SourceOrdinal),
                                ("$statusOrdinal", statusOrdinal),
                                ("$requiredStatus", requirement.RequiredStatuses[statusOrdinal])).ConfigureAwait(false);
                        }
                    }

                    foreach (var objective in task.Objectives.Concat(task.FailureConditions))
                    {
                        await PersistObjectiveAsync(
                            connection,
                            transaction,
                            provenance,
                            objective,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                // Nothing is recorded here about progress the catalog no longer carries. It
                // used to be, into quest_catalog_orphans, which nothing ever read: the Quests
                // page answers the same question live, off the profile and the catalog that is
                // actually loaded, and covers item holdings and pins as well. 0010 drops it.
            },
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    private static async Task PersistObjectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestCatalogProvenance provenance,
        QuestObjectiveDefinition objective,
        CancellationToken cancellationToken)
    {
        var failure = objective.IsFailureCondition;
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO quest_catalog_objectives(
                source_key, source_mode, language, task_id, id, is_failure_condition,
                source_ordinal, source_type, normalized_kind, is_unsupported, description,
                target_count, optional, found_in_raid_required, target_task_id,
                subtype_json, raw_json)
            VALUES (
                $sourceKey, $sourceMode, $language, $taskId, $id, $isFailure,
                $sourceOrdinal, $sourceType, $normalizedKind, $isUnsupported, $description,
                $targetCount, $optional, $foundInRaid, $targetTaskId,
                $subtypeJson, $rawJson);
            """,
            cancellationToken,
            ("$sourceKey", provenance.Source),
            ("$sourceMode", provenance.SourceMode),
            ("$language", provenance.Language),
            ("$taskId", objective.TaskId),
            ("$id", objective.Id),
            ("$isFailure", failure),
            ("$sourceOrdinal", objective.SourceOrdinal),
            ("$sourceType", objective.SourceType),
            ("$normalizedKind", objective.Kind.ToString()),
            ("$isUnsupported", objective.IsUnsupported),
            ("$description", objective.Description),
            ("$targetCount", FormatCount(objective.TargetCount)),
            ("$optional", objective.Optional),
            ("$foundInRaid", objective.FoundInRaidRequired),
            ("$targetTaskId", objective.TargetTaskId),
            ("$subtypeJson", objective.SubtypeJson),
            ("$rawJson", objective.RawSourceJson)).ConfigureAwait(false);

        if (!failure)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO task_objectives(id, task_id, type, description, map_id, zone_json)
                VALUES ($id, $taskId, $type, $description, $mapId, $zoneJson);
                """,
                cancellationToken,
                ("$id", objective.Id),
                ("$taskId", objective.TaskId),
                ("$type", objective.SourceType),
                ("$description", objective.Description),
                ("$mapId", objective.MapAssociations.FirstOrDefault(link => link.Kind == QuestMapAssociationKind.Declared)?.MapId),
                ("$zoneJson", JsonSerializer.Serialize(objective.Zones, SerializerOptions))).ConfigureAwait(false);
        }

        for (var statusOrdinal = 0; statusOrdinal < objective.TargetStatuses.Count; statusOrdinal++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO quest_objective_target_statuses(
                    source_key, source_mode, language, task_id, objective_id,
                    is_failure_condition, status_ordinal, target_status)
                VALUES (
                    $sourceKey, $sourceMode, $language, $taskId, $objectiveId,
                    $isFailure, $statusOrdinal, $targetStatus);
                """,
                cancellationToken,
                ("$sourceKey", provenance.Source),
                ("$sourceMode", provenance.SourceMode),
                ("$language", provenance.Language),
                ("$taskId", objective.TaskId),
                ("$objectiveId", objective.Id),
                ("$isFailure", failure),
                ("$statusOrdinal", statusOrdinal),
                ("$targetStatus", objective.TargetStatuses[statusOrdinal])).ConfigureAwait(false);
        }

        foreach (var link in objective.MapAssociations)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO quest_objective_map_links(
                    source_key, source_mode, language, task_id, objective_id,
                    is_failure_condition, association_kind, source_ordinal, map_id)
                VALUES (
                    $sourceKey, $sourceMode, $language, $taskId, $objectiveId,
                    $isFailure, $associationKind, $sourceOrdinal, $mapId);
                """,
                cancellationToken,
                ("$sourceKey", provenance.Source),
                ("$sourceMode", provenance.SourceMode),
                ("$language", provenance.Language),
                ("$taskId", objective.TaskId),
                ("$objectiveId", objective.Id),
                ("$isFailure", failure),
                ("$associationKind", link.Kind.ToString()),
                ("$sourceOrdinal", link.SourceOrdinal),
                ("$mapId", link.MapId)).ConfigureAwait(false);
        }

        foreach (var target in objective.ItemTargets)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO quest_objective_item_targets(
                    source_key, source_mode, language, task_id, objective_id,
                    is_failure_condition, source_field, alternative_group, source_ordinal,
                    item_id, target_count, found_in_raid_required)
                VALUES (
                    $sourceKey, $sourceMode, $language, $taskId, $objectiveId,
                    $isFailure, $sourceField, $alternativeGroup, $sourceOrdinal,
                    $itemId, $targetCount, $foundInRaid);
                """,
                cancellationToken,
                ("$sourceKey", provenance.Source),
                ("$sourceMode", provenance.SourceMode),
                ("$language", provenance.Language),
                ("$taskId", objective.TaskId),
                ("$objectiveId", objective.Id),
                ("$isFailure", failure),
                ("$sourceField", target.SourceField),
                ("$alternativeGroup", target.AlternativeGroup),
                ("$sourceOrdinal", target.SourceOrdinal),
                ("$itemId", target.ItemId),
                ("$targetCount", FormatCount(target.TargetCount)),
                ("$foundInRaid", target.FoundInRaidRequired)).ConfigureAwait(false);

            if (!failure && target.SourceField == "items")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR REPLACE INTO task_objective_items(
                        task_id, objective_id, item_id, count, found_in_raid_required)
                    VALUES ($taskId, $objectiveId, $itemId, $count, $foundInRaid);
                    """,
                    cancellationToken,
                    ("$taskId", objective.TaskId),
                    ("$objectiveId", objective.Id),
                    ("$itemId", target.ItemId),
                    ("$count", objective.TargetCount ?? 1),
                    ("$foundInRaid", objective.FoundInRaidRequired == true)).ConfigureAwait(false);
            }
        }

        foreach (var zone in objective.Zones)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO quest_objective_zones(
                    source_key, source_mode, language, task_id, objective_id,
                    is_failure_condition, source_ordinal, source_zone_id, map_id,
                    position_x, position_y, position_z, outline_json,
                    bottom_elevation, top_elevation, terrain_elevation,
                    size_json, name, raw_json)
                VALUES (
                    $sourceKey, $sourceMode, $language, $taskId, $objectiveId,
                    $isFailure, $sourceOrdinal, $sourceZoneId, $mapId,
                    $positionX, $positionY, $positionZ, $outlineJson,
                    $bottomElevation, $topElevation, $terrainElevation,
                    $sizeJson, $name, $rawJson);
                """,
                cancellationToken,
                ("$sourceKey", provenance.Source),
                ("$sourceMode", provenance.SourceMode),
                ("$language", provenance.Language),
                ("$taskId", objective.TaskId),
                ("$objectiveId", objective.Id),
                ("$isFailure", failure),
                ("$sourceOrdinal", zone.SourceOrdinal),
                ("$sourceZoneId", zone.SourceZoneId),
                ("$mapId", zone.MapId),
                ("$positionX", zone.Position?.X),
                ("$positionY", zone.Position?.Y),
                ("$positionZ", zone.Position?.Z),
                ("$outlineJson", JsonSerializer.Serialize(zone.Outline, SerializerOptions)),
                ("$bottomElevation", zone.BottomElevation),
                ("$topElevation", zone.TopElevation),
                ("$terrainElevation", zone.TerrainElevation),
                ("$sizeJson", zone.Size is null ? null : JsonSerializer.Serialize(zone.Size.Value, SerializerOptions)),
                ("$name", zone.Name),
                ("$rawJson", zone.RawSourceJson)).ConfigureAwait(false);
        }
    }

    public Task RefreshHideoutAsync(
        IReadOnlyDictionary<string, TarkovDevHideoutStation> data,
        CancellationToken cancellationToken) =>
        RefreshHideoutWithCommitAsync(data, null, cancellationToken);

    internal async Task RefreshHideoutWithCommitAsync(
        IReadOnlyDictionary<string, TarkovDevHideoutStation> data,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "hideout");
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
                                requirement.RequiredLevel,
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
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    public Task RefreshTradersAsync(
        IReadOnlyDictionary<string, TarkovDevTrader> data,
        CancellationToken cancellationToken) =>
        RefreshTradersWithCommitAsync(data, null, cancellationToken);

    internal async Task RefreshTradersWithCommitAsync(
        IReadOnlyDictionary<string, TarkovDevTrader> data,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "traders");
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
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    public Task RefreshCraftsAsync(
        IReadOnlyList<TarkovDevCraft> data,
        CancellationToken cancellationToken) =>
        RefreshCraftsWithCommitAsync(data, null, cancellationToken);

    internal async Task RefreshCraftsWithCommitAsync(
        IReadOnlyList<TarkovDevCraft> data,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "crafts");
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
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    public Task RefreshBartersAsync(
        IReadOnlyList<TarkovDevBarter> data,
        CancellationToken cancellationToken) =>
        RefreshBartersWithCommitAsync(data, null, cancellationToken);

    internal async Task RefreshBartersWithCommitAsync(
        IReadOnlyList<TarkovDevBarter> data,
        DataRefreshCommitAction? commitAction,
        CancellationToken cancellationToken)
    {
        TarkovDevDatasetValidator.Validate(data, "barters");
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
            cancellationToken,
            commitAction).ConfigureAwait(false);
    }

    public async Task<PriceHistoryRefreshOutcome> RefreshPriceHistoryAsync(
        string itemId,
        IReadOnlyList<TarkovDevPricePoint> points,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        TarkovDevDatasetValidator.Validate(points, $"prices/{itemId}");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // This no-op update is deliberately the first database statement. It promotes the
        // deferred transaction to SQLite's single writer before it also checks existence, so a
        // concurrent catalog deletion either wins first (and produces the classified outcome
        // below) or waits until every history row is committed. A read-then-write sequence could
        // otherwise lose the item between those steps and surface SQLITE_BUSY or a foreign-key
        // exception instead of a stable result.
        var catalogMatches = await ExecuteAsync(
            connection,
            transaction,
            "UPDATE items SET id = id WHERE id = $itemId;",
            cancellationToken,
            ("$itemId", itemId)).ConfigureAwait(false);
        if (catalogMatches == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PriceHistoryRefreshOutcome.ItemNotInCatalog;
        }

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM price_history_unresolved_time WHERE item_id = $itemId AND source = 'json.tarkov.dev/prices';",
            cancellationToken,
            ("$itemId", itemId)).ConfigureAwait(false);

        foreach (var (point, ordinal) in points.Select((value, index) => (value, index)))
        {
            if (point.Timestamp is not { } timestamp)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO price_history_unresolved_time(
                        item_id, source_ordinal, flea_price, trader_value, source, raw_json)
                    VALUES ($itemId, $ordinal, $fleaPrice, NULL, 'json.tarkov.dev/prices', $rawJson);
                    """,
                    cancellationToken,
                    ("$itemId", itemId),
                    ("$ordinal", ordinal),
                    ("$fleaPrice", point.Price ?? point.PriceMin),
                    ("$rawJson", JsonSerializer.Serialize(point, SerializerOptions))).ConfigureAwait(false);
                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
                VALUES ($itemId, $timestampUtc, $fleaPrice, NULL, 'json.tarkov.dev/prices');
                """,
                cancellationToken,
                ("$itemId", itemId),
                ("$timestampUtc", FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(timestamp))),
                ("$fleaPrice", point.Price ?? point.PriceMin)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PriceHistoryRefreshOutcome.Updated;
    }

    private async Task InTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken cancellationToken,
        DataRefreshCommitAction? commitAction = null)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await action(connection, transaction).ConfigureAwait(false);
        if (commitAction is not null)
        {
            await commitAction(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// How many rows an endpoint's main table already holds.
    /// </summary>
    /// <remarks>
    /// So a refresh can be refused before it deletes anything. Every refresh here replaces its
    /// table wholesale — RefreshMapsAsync opens with DELETE FROM maps, and the item refresh
    /// deletes every row not in the incoming set — and the validators pass trivially on an
    /// empty payload, so one bad response emptied the catalog and the application then said,
    /// accurately, that it had no data.
    ///
    /// Returns null for an endpoint with no table worth counting, which is not a refusal.
    /// </remarks>
    public async Task<int?> CountRowsAsync(string endpoint, CancellationToken cancellationToken)
    {
        var table = endpoint switch
        {
            "items" => "items",
            "maps" => "maps",
            "tasks" => "tasks",
            "hideout" => "hideout_stations",
            "traders" => "traders",
            "crafts" => "crafts",
            "barters" => "barters",
            _ => null,
        };
        if (table is null)
        {
            return null;
        }

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            // A table that does not exist yet is a first run, not a reason to refuse.
            return null;
        }
    }

    /// <summary>
    /// What to store when upstream stopped sending a name.
    /// </summary>
    /// <remarks>
    /// The id, which is this application's standing rule for a name it does not have: the
    /// trader lookup's own remark says "wrong is worse than ugly, and a trader the catalog does
    /// not know about" prints as its id, and #242 records the same decision for two dozen exits
    /// upstream names with internal tokens.
    ///
    /// It exists because these columns are NOT NULL. Relaxing the required markers on the
    /// models without this would have moved the failure from a deserialiser that throws to an
    /// INSERT that throws halfway through a refresh transaction, which is strictly worse.
    /// </remarks>
    private static string Named(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : name;

    /// <summary>Builds an injective opaque key from untrusted upstream identity components.</summary>
    /// <remarks>
    /// Delimiter concatenation is ambiguous because upstream ids may contain that delimiter:
    /// <c>("a", "b:c")</c> and <c>("a:b", "c")</c> used to produce the same map-lock key and
    /// poison the already-published HTTP cache when normalized persistence rolled back. Prefixing
    /// every component with its UTF-8 byte length preserves the complete tuple without restricting
    /// otherwise valid upstream ids.
    /// </remarks>
    private static string CompositeIdentity(params string[] components) => string.Concat(
        components.Select(component => FormattableString.Invariant(
            $"{Encoding.UTF8.GetByteCount(component)}:{component}")));

    /// <summary>
    /// Returns the short name to persist, falling back to the full name when it is blank.
    /// </summary>
    /// <remarks>
    /// One live item currently carries an empty short name. Rejecting the whole batch over a
    /// single cosmetic field meant a working download produced an empty item catalog, so the
    /// item is stored under its full name instead.
    /// </remarks>
    private static string ResolveShortName(TarkovDevItem item) =>
        string.IsNullOrWhiteSpace(item.ShortName) ? item.Name : item.ShortName;

    private static string? GetString(JsonElement? element, string propertyName) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? GetFiniteDouble(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (!property.TryGetDouble(out var result) || !double.IsFinite(result) || result < 0)
        {
            throw new InvalidDataException($"Item property '{propertyName}' must be a finite non-negative number when present.");
        }

        return result;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatCount(decimal? count) =>
        count?.ToString(CultureInfo.InvariantCulture);

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
        offers
            .Select(offer => offer.PriceRub is > 0 ? offer.PriceRub : offer.Price)
            .OfType<long>()
            .DefaultIfEmpty()
            .Max() is var value && value > 0 ? value : null;
}
