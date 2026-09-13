using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Projects the synced <c>items</c> and <c>map_locks</c> tables into the fact tables the
/// intelligence services are constructed from.
/// </summary>
/// <remarks>
/// Every projection is a full scan of the item table, so each one is loaded once and kept until
/// <see cref="Invalidate"/> is called at the end of a sync. Anything the upstream payload does
/// not state is left absent rather than defaulted: the services read a missing caliber, weight
/// or lock as "no data" and skip the check, whereas a fabricated value produces a confident
/// wrong answer. A single malformed <c>properties_json</c> only drops its own item.
/// </remarks>
public sealed class SqliteItemFactCatalog(SqliteConnectionFactory connectionFactory) : IItemFactCatalog
{
    private const string SourceKey = "json.tarkov.dev/items";
    private const string AmmoPropertiesType = "ItemPropertiesAmmo";
    private const string WeaponPropertiesType = "ItemPropertiesWeapon";

    // Ammunition below the speed of sound at sea level is what the game treats as subsonic.
    private const double SpeedOfSoundMetresPerSecond = 343;

    // These rows are copied from the upstream payload rather than inferred, so they are close to
    // certain; the small discount records that the local copy is only as fresh as the last sync.
    private static readonly Confidence UpstreamFact = new(0.95);
    private static readonly IReadOnlySet<string> NoItemIds = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyList<string> NoLockIds = [];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AmmoStats>? _ammo;
    private IReadOnlyList<AmmoPackContents>? _ammoPacks;
    private IReadOnlyList<LoadoutItemFacts>? _loadoutFacts;
    private IReadOnlyList<KeyFacts>? _keyFacts;
    private int _generation;

    /// <summary>Ballistic facts for every item carrying ammunition properties.</summary>
    /// <remarks>
    /// An ammunition row without a caliber, damage or penetration figure is dropped instead of
    /// zero-filled: those three drive the caliber ranking and the armor table, and a zeroed round
    /// would be presented as a real, terrible round. json.tarkov.dev states no subsonic flag, so
    /// it is derived from muzzle velocity.
    /// </remarks>
    public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<AmmoStats>(() => _ammo, value => _ammo = value, LoadAmmoAsync, cancellationToken);

    /// <summary>Resolves ammunition boxes to the round they unpack into.</summary>
    /// <remarks>
    /// A pack is recognised only when its contents name exactly one distinct item and that item
    /// is itself ammunition. Mixed or unreadable contents are skipped, because the contract of
    /// this table is a one-to-one mapping and a guess here silently redirects every ammunition
    /// lookup for that box.
    /// </remarks>
    public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<AmmoPackContents>(
            () => _ammoPacks,
            value => _ammoPacks = value,
            LoadAmmoPacksAsync,
            cancellationToken);

    /// <summary>Cost, weight and compatibility facts for every known item.</summary>
    /// <remarks>
    /// <para>
    /// <c>CompatibleWeaponItemIds</c> is always empty. It is read as "the weapons this magazine
    /// fits", and nothing in the synced data expresses that: a magazine states its capacity and
    /// the ammunition it accepts, never its host weapons. Filling it with the magazine's
    /// <c>allowedAmmo</c> would make <c>CheckMagazines</c> compare a weapon id against a list of
    /// ammunition ids and report every magazine as incompatible, so the set is left empty and the
    /// check correctly abstains.
    /// </para>
    /// <para>
    /// <c>CompatibleParentItemIds</c> is read as "the parents this plate fits into", which is
    /// equally underivable for plates, so armor and weapons get an empty set. It is populated for
    /// ammunition only, by inverting the <c>allowedAmmo</c> lists of every weapon and magazine:
    /// those genuinely are the parents a round can be loaded into. No current check reads it for
    /// ammunition, so the inversion cannot make a check pass falsely; it is stated because it is
    /// true, not to satisfy a check.
    /// </para>
    /// <para>
    /// Weight has no column of its own and is parsed out of <c>raw_json</c>, where the sync keeps
    /// the upstream item's extra top-level fields. An item whose payload omits it reports 0, which
    /// <c>LoadoutEvaluation</c> surfaces as an under-count of the kit; a dedicated
    /// <c>items.weight</c> column would make this both cheaper and complete.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<LoadoutItemFacts>(
            () => _loadoutFacts,
            value => _loadoutFacts = value,
            LoadLoadoutFactsAsync,
            cancellationToken);

    /// <summary>Lock, use-count and cost facts for every key.</summary>
    /// <remarks>
    /// <para>
    /// WARNING: the weighted score <c>KeyIntelligenceService</c> builds from these facts is not
    /// meaningful yet and the UI must not render a tier from it. Four of its six inputs -
    /// expected loot, utility, unique access and route risk - have no source in the synced data
    /// and are reported here as zero, and quest linkage is not wired up, so the score reduces to
    /// "how many locks does it open and how many uses does it have". Every key will read as a low
    /// tier for that reason alone. A tier becomes presentable once there is somewhere curated
    /// facts can come from; there is not. This used to point at a <c>key_intelligence_overrides</c>
    /// table and say the service already preferred it, which was wrong twice over: nothing read
    /// that table and nothing ever wrote to it. Migration 0007 dropped it.
    /// </para>
    /// <para>
    /// A key that appears on more than one map reports no map rather than an arbitrary one, but
    /// still lists every lock it opens.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<KeyFacts>(() => _keyFacts, value => _keyFacts = value, LoadKeyFactsAsync, cancellationToken);

    public void Invalidate()
    {
        // The stamp moves before the fields are cleared so that a projection already reading the
        // pre-sync database cannot store its result afterwards.
        Interlocked.Increment(ref _generation);
        _ammo = null;
        _ammoPacks = null;
        _loadoutFacts = null;
        _keyFacts = null;
    }

    private async Task<IReadOnlyList<T>> GetOrLoadAsync<T>(
        Func<IReadOnlyList<T>?> read,
        Action<IReadOnlyList<T>> store,
        Func<SqliteConnection, CancellationToken, Task<IReadOnlyList<T>>> load,
        CancellationToken cancellationToken)
    {
        var cached = read();
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = read();
            if (cached is not null)
            {
                return cached;
            }

            var generation = _generation;
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var loaded = await load(connection, cancellationToken).ConfigureAwait(false);
            if (generation == _generation)
            {
                store(loaded);
            }

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IReadOnlyList<AmmoStats>> LoadAmmoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, properties_json, source_updated_utc
            FROM items
            WHERE properties_type = $propertiesType AND properties_json IS NOT NULL
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$propertiesType", AmmoPropertiesType);
        var ammo = new List<AmmoStats>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = TryParse(reader.GetString(1));
            if (document is null)
            {
                continue;
            }

            var properties = document.RootElement;
            var caliber = ReadString(properties, "caliber");
            var damage = ReadInt32(properties, "damage");
            var penetration = ReadInt32(properties, "penetrationPower");
            if (string.IsNullOrWhiteSpace(caliber) || damage is null || penetration is null)
            {
                continue;
            }

            var velocity = ReadDouble(properties, "initialSpeed");
            ammo.Add(new(
                reader.GetString(0),
                caliber,
                damage.Value,
                penetration.Value,
                ReadInt32(properties, "armorDamage"),
                ReadDouble(properties, "fragmentationChance"),
                // A round that states no projectile count fires one; zero would read as a round
                // that fires nothing.
                ReadInt32(properties, "projectileCount") ?? 1,
                velocity,
                ReadDouble(properties, "recoilModifier"),
                ReadDouble(properties, "accuracyModifier"),
                ReadBoolean(properties, "tracer"),
                velocity is { } speed && speed < SpeedOfSoundMetresPerSecond,
                UpstreamProvenance(ParseTimestamp(reader.GetString(2)))));
        }

        return ammo;
    }

    private static async Task<IReadOnlyList<AmmoPackContents>> LoadAmmoPacksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var ammoItemIds = await LoadAmmoItemIdsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (ammoItemIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        // Pack contents exist only in raw_json, which is a multi-kilobyte document per item. The
        // substring narrows that scan to the rows that could possibly be packs; the parse below,
        // not the match, decides which of them actually are.
        command.CommandText = """
            SELECT id, raw_json, source_updated_utc
            FROM items
            WHERE raw_json IS NOT NULL AND raw_json LIKE '%containsItems%'
            ORDER BY id;
            """;
        var packs = new List<AmmoPackContents>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = TryParse(reader.GetString(1));
            if (document is null ||
                document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("containsItems", out var contents) ||
                contents.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var contained = ReadSingleContainedItem(contents);
            if (contained is not { } pack || !ammoItemIds.Contains(pack.ItemId))
            {
                continue;
            }

            packs.Add(new(
                reader.GetString(0),
                pack.ItemId,
                pack.Quantity,
                UpstreamProvenance(ParseTimestamp(reader.GetString(2)))));
        }

        return packs;
    }

    private static async Task<IReadOnlyList<LoadoutItemFacts>> LoadLoadoutFactsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, category_type, last_low_price, avg_24h_price, base_price,
                   properties_type, properties_json, raw_json
            FROM items
            ORDER BY id;
            """;
        var rows = new List<LoadoutRow>();
        var ammoItemIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = reader.GetString(0);
                var propertiesType = reader.IsDBNull(6) ? null : reader.GetString(6);
                using var properties = reader.IsDBNull(7) ? null : TryParse(reader.GetString(7));
                using var rawItem = reader.IsDBNull(8) ? null : TryParse(reader.GetString(8));
                var isAmmo = string.Equals(propertiesType, AmmoPropertiesType, StringComparison.Ordinal);
                if (isAmmo)
                {
                    ammoItemIds.Add(itemId);
                }

                var carriesCaliber = isAmmo ||
                    string.Equals(propertiesType, WeaponPropertiesType, StringComparison.Ordinal);
                rows.Add(new(
                    itemId,
                    reader.GetString(1),
                    ParseCategory(reader.GetString(2)),
                    BestPriceRoubles(reader, 3, 4, 5),
                    ReadWeightKg(rawItem?.RootElement),
                    carriesCaliber ? ReadString(properties?.RootElement, "caliber") : null,
                    ReadAllowedAmmo(properties?.RootElement)));
            }
        }

        var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var ammoItemId in row.AllowedAmmoItemIds)
            {
                // Only ammunition ids are inverted, so a weapon or a plate can never acquire a
                // parent from a malformed list.
                if (!ammoItemIds.Contains(ammoItemId))
                {
                    continue;
                }

                if (!parents.TryGetValue(ammoItemId, out var holders))
                {
                    holders = new(StringComparer.Ordinal);
                    parents[ammoItemId] = holders;
                }

                holders.Add(row.Id);
            }
        }

        var facts = new List<LoadoutItemFacts>(rows.Count);
        foreach (var row in rows)
        {
            facts.Add(new(
                ItemId: row.Id,
                Name: row.Name,
                Category: row.Category,
                ApproximateCostRoubles: row.CostRoubles,
                WeightKg: row.WeightKg,
                Caliber: row.Caliber,
                CompatibleWeaponItemIds: NoItemIds,
                CompatibleParentItemIds: parents.TryGetValue(row.Id, out var containers) ? containers : NoItemIds));
        }

        return facts;
    }

    private static async Task<IReadOnlyList<KeyFacts>> LoadKeyFactsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var locks = await LoadKeyLocksAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, properties_json, last_low_price, avg_24h_price, base_price, source_updated_utc
            FROM items
            WHERE category_type = $categoryType
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$categoryType", nameof(ItemCategory.Key));
        var keys = new List<KeyFacts>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = reader.GetString(0);
            using var properties = reader.IsDBNull(1) ? null : TryParse(reader.GetString(1));
            var opened = locks.GetValueOrDefault(itemId);
            var lockIds = opened?.LockIds ?? NoLockIds;
            string? mapId = opened is { MapIds.Count: 1 } onlyMap ? onlyMap.MapIds[0] : null;
            keys.Add(new(
                ItemId: itemId,
                MapId: mapId,
                MaximumUses: ReadInt32(properties?.RootElement, "uses"),
                Locks: lockIds,
                // Quest linkage lives in task_objectives and is a separate change; an empty list
                // scores the quest component as zero instead of inventing a task.
                RelevantTaskIds: [],
                AcquisitionCostRoubles: BestPriceRoubles(reader, 2, 3, 4),
                // The synced payload states none of the following four, and every one of them is
                // a score input. Zero is the honest reading of "unknown" and is why the remark on
                // GetKeyFactsAsync forbids showing a tier.
                ExpectedLootRoubles: 0,
                Utility: 0,
                GrantsUniqueAccess: false,
                RouteRisk: 0,
                Provenance: UpstreamProvenance(ParseTimestamp(reader.GetString(5)))));
        }

        return keys;
    }

    private static async Task<Dictionary<string, KeyLocks>> LoadKeyLocksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_item_id, id, map_id
            FROM map_locks
            WHERE key_item_id IS NOT NULL
            ORDER BY key_item_id, id;
            """;
        var builders = new Dictionary<string, (List<string> LockIds, List<string> MapIds)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var keyItemId = reader.GetString(0);
            if (!builders.TryGetValue(keyItemId, out var entry))
            {
                entry = (new List<string>(), new List<string>());
                builders[keyItemId] = entry;
            }

            entry.LockIds.Add(reader.GetString(1));
            var mapId = reader.GetString(2);
            if (!entry.MapIds.Contains(mapId, StringComparer.Ordinal))
            {
                entry.MapIds.Add(mapId);
            }
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => new KeyLocks(pair.Value.LockIds, pair.Value.MapIds),
            StringComparer.Ordinal);
    }

    private static async Task<HashSet<string>> LoadAmmoItemIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items WHERE properties_type = $propertiesType;";
        command.Parameters.AddWithValue("$propertiesType", AmmoPropertiesType);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            itemIds.Add(reader.GetString(0));
        }

        return itemIds;
    }

    private static (string ItemId, int Quantity)? ReadSingleContainedItem(JsonElement containsItems)
    {
        string? containedId = null;
        var quantity = 0;
        foreach (var entry in containsItems.EnumerateArray())
        {
            var entryId = ReadContainedItemId(entry);
            var count = ReadInt32(entry, "count");
            if (entryId is null || count is null or <= 0)
            {
                return null;
            }

            if (containedId is not null && !StringComparer.Ordinal.Equals(containedId, entryId))
            {
                return null;
            }

            containedId = entryId;
            quantity += count.Value;
        }

        return containedId is null ? null : (containedId, quantity);
    }

    private static string? ReadContainedItemId(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("item", out var item))
        {
            return null;
        }

        // The flat payload writes the contained item as a bare id; accepting the nested
        // {"item":{"id":...}} shape as well costs nothing and keeps a schema change from
        // quietly emptying this table.
        var itemId = item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Object => ReadString(item, "id"),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(itemId) ? null : itemId;
    }

    private static IReadOnlyList<string> ReadAllowedAmmo(JsonElement? properties)
    {
        if (properties is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("allowedAmmo", out var allowedAmmo) ||
            allowedAmmo.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var itemIds = new List<string>();
        foreach (var entry in allowedAmmo.EnumerateArray())
        {
            var itemId = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object => ReadString(entry, "id"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                itemIds.Add(itemId);
            }
        }

        return itemIds;
    }

    /// <summary>Reads the upstream item weight, which has no column of its own yet.</summary>
    /// <remarks>
    /// TODO: read this from an <c>items.weight</c> column once one exists. Weight is a top-level
    /// field of the upstream item, not part of <c>properties</c>, and survives into
    /// <c>raw_json</c> only because the sync round-trips unmapped fields; a payload that omits it
    /// yields 0 rather than an estimate.
    /// </remarks>
    private static double ReadWeightKg(JsonElement? rawItem) =>
        ReadDouble(rawItem, "weight") is { } weight && weight > 0 ? weight : 0;

    /// <summary>Picks the rouble figure a player would actually pay for an item.</summary>
    /// <remarks>
    /// The most recent flea sale is what the item costs in practice, so it wins; the 24-hour
    /// average only stands in when the flea has not traded the item today, and the base price is
    /// the game's internal value rather than a market one, kept as the last resort because it is
    /// the only figure a flea-banned item has. A non-positive column means "not priced", not
    /// "free", and falls through to the next source.
    /// </remarks>
    private static long BestPriceRoubles(DbDataReader reader, int fleaOrdinal, int averageOrdinal, int baseOrdinal) =>
        PositivePrice(reader, fleaOrdinal) ??
        PositivePrice(reader, averageOrdinal) ??
        PositivePrice(reader, baseOrdinal) ??
        0;

    private static long? PositivePrice(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var price = reader.GetInt64(ordinal);
        return price > 0 ? price : null;
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? element, string propertyName) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (property.TryGetInt32(out var number))
        {
            return number;
        }

        // These are whole numbers upstream, but one written as 34.0 fails TryGetInt32 outright,
        // and rounding it keeps the stat instead of dropping the round.
        return property.TryGetDouble(out var fractional) &&
            fractional is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(fractional)
            : null;
    }

    private static double? ReadDouble(JsonElement? element, string propertyName) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var number)
            ? number
            : null;

    private static bool ReadBoolean(JsonElement? element, string propertyName) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static ItemCategory ParseCategory(string value) =>
        Enum.TryParse<ItemCategory>(value, true, out var category) ? category : ItemCategory.Unknown;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DataProvenance UpstreamProvenance(DateTimeOffset sourceUpdatedUtc) =>
        new(SourceKey, sourceUpdatedUtc, sourceUpdatedUtc, Confidence: UpstreamFact);

    private sealed record LoadoutRow(
        string Id,
        string Name,
        ItemCategory Category,
        long CostRoubles,
        double WeightKg,
        string? Caliber,
        IReadOnlyList<string> AllowedAmmoItemIds);

    private sealed record KeyLocks(IReadOnlyList<string> LockIds, IReadOnlyList<string> MapIds);
}
