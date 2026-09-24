using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

/// <summary>The exact map-catalog coordinate space used by one normalized snapshot.</summary>
public sealed record TarkovDevLootSpawnMapBinding
{
    public TarkovDevLootSpawnMapBinding(
        string mapId,
        string variantKey,
        string transformVersion,
        MapSceneBounds? sceneBounds,
        IReadOnlyList<string> sourceMapIds,
        IReadOnlyList<string> floorIds,
        MapCatalogProvenance provenance)
    {
        MapId = Required(mapId, nameof(mapId), 128);
        VariantKey = Required(variantKey, nameof(variantKey), 128);
        TransformVersion = Required(transformVersion, nameof(transformVersion), 128);
        SceneBounds = sceneBounds;
        SourceMapIds = Copy(sourceMapIds, 32, nameof(sourceMapIds), 128);
        FloorIds = Copy(floorIds, LootSpawnLocation.MaximumFloors, nameof(floorIds), 96);
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public string MapId { get; }

    public string VariantKey { get; }

    public string TransformVersion { get; }

    /// <summary>Null means this catalog variant cannot honestly project a source world point.</summary>
    public MapSceneBounds? SceneBounds { get; }

    /// <summary>The json.tarkov.dev normalized map IDs folded into this canonical location.</summary>
    public IReadOnlyList<string> SourceMapIds { get; }

    public IReadOnlyList<string> FloorIds { get; }

    public MapCatalogProvenance Provenance { get; }

    private static IReadOnlyList<string> Copy(
        IReadOnlyList<string> values,
        int maximum,
        string parameterName,
        int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var bounded = values.Take(maximum + 1).ToArray();
        if (bounded.Length > maximum)
        {
            throw new ArgumentException($"The {parameterName} collection is oversized.", parameterName);
        }

        var copied = bounded
            .Select(value => Required(value, parameterName, maximumLength))
            .ToArray();
        if (copied.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Length)
        {
            throw new ArgumentException($"The {parameterName} collection contains aliases or duplicates.", parameterName);
        }

        Array.Sort(copied, StringComparer.Ordinal);
        return Array.AsReadOnly(copied);
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !value.IsNormalized(NormalizationForm.FormC) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed record TarkovDevLootSpawnNormalizationRequest(
    GameMode GameMode,
    string Language,
    DateTimeOffset ImportedUtc,
    TarkovDevResponse<TarkovDevMapsData> Maps,
    TarkovDevResponse<TarkovDevItemsData> Items,
    string MapCatalogJson,
    TarkovDevMapCatalog MapCatalog);

public sealed record TarkovDevLootSpawnNormalizationResult(
    LootSpawnSourceBundle Bundle,
    IReadOnlyList<TarkovDevLootSpawnMapBinding> MapBindings);

/// <summary>
/// Joins the validated production maps and item responses to the separately reviewed map catalog.
/// </summary>
/// <remarks>
/// json.tarkov.dev supplies loose-loot membership and world positions, but no probability,
/// respawn rule, floor ID, or container contents. This adapter preserves those boundaries: item
/// pools remain explicitly unweighted, probability and respawn remain unknown, containers count
/// toward measured source coverage without becoming fake item pools, and an unprojectable world
/// point becomes map-only knowledge instead of a marker at an invented coordinate.
/// </remarks>
public sealed class TarkovDevLootSpawnNormalizer
{
    public const int MaximumSourceMaps = LootSpawnSourceImportContext.MaximumMaps;

    public const int MaximumKnownRecords = 32_768;

    public const int MaximumBundleCandidates = 65_536;

    private const int MaximumVariantsPerLocation = 64;
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private const int MaximumMapCatalogBytes = 4 * 1024 * 1024;
    private const string MapCatalogPathSuffix = "/src/data/maps.json";
    private const string SourceTerms =
        "tarkov.dev structured-data attribution; tarkov-api repository GPL-3.0";

    private static readonly ProducerIdentity Producer = new(
        "Tarkov Companion json.tarkov.dev loot normalizer",
        "1");

    private static readonly JsonSerializerOptions SourceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64,
    };

    public ValueTask<TarkovDevLootSpawnNormalizationResult> NormalizeAsync(
        TarkovDevLootSpawnNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(Normalize(request, cancellationToken));
        }
        catch (LootSpawnSourceImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or OverflowException)
        {
            throw Refused(
                "tarkov-dev.normalization-invalid",
                "The production loot-spawn inputs violate the bounded normalization contract.",
                exception);
        }
    }

    private static TarkovDevLootSpawnNormalizationResult Normalize(
        TarkovDevLootSpawnNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        request = BindExactDocuments(request, cancellationToken);
        ValidateTopLevelBudgets(request);
        ValidateAggregateBudgets(request, cancellationToken);
        TarkovDevDatasetValidator.Validate(request.Maps.Data, request.Maps.SourceKey!);
        TarkovDevDatasetValidator.Validate(request.Items.Data, request.Items.SourceKey!);

        var mode = ModeSlug(request.GameMode);
        var sourceIdentifier = $"json.tarkov.dev/{mode}/maps";
        var sourceReference = $"https://json.tarkov.dev/{mode}/maps";
        var itemIdentifier = $"json.tarkov.dev/{mode}/items";
        var itemReference = $"https://json.tarkov.dev/{mode}/items";
        var sourceFreshness = request.Maps.IsStale ||
                              request.MapCatalog.Provenance.Availability == MapCatalogAvailability.OfflineCached
            ? FreshnessState.Stale
            : FreshnessState.Current;
        var itemFreshness = request.Items.IsStale ? FreshnessState.Stale : FreshnessState.Current;
        // [#799] Clamped to the import time: a response cached while the clock was ahead carries
        // a "future" stamp, and the source identity requires data-through <= import.
        var dataThroughUtc = Earlier(
            Earlier(
                DataThrough(request.Maps),
                request.MapCatalog.Provenance.RetrievedUtc.ToUniversalTime()),
            request.ImportedUtc);
        var contentHash = CompositeContentHash(request, mode, cancellationToken);
        var datasetVersion = $"json-tarkov-dev-v1-{contentHash[..24]}";
        var confidence = EvidenceConfidence.Unscored;

        var bindingDrafts = BuildBindings(request.MapCatalog, cancellationToken);
        var aliasIndex = BuildAliasIndex(bindingDrafts);
        var sourceMaps = request.Maps.Data.Maps.Values
            .OrderBy(map => map.Id, StringComparer.Ordinal)
            .ToArray();
        var sourceMapsByBinding = new Dictionary<string, List<TarkovDevMap>>(StringComparer.Ordinal);
        var unmatchedSourceMaps = 0;
        foreach (var sourceMap in sourceMaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CanonicalOptional(sourceMap.NormalizedName, 128) is not { } normalizedMapId ||
                !aliasIndex.TryGetValue(normalizedMapId, out var binding))
            {
                unmatchedSourceMaps++;
                continue;
            }

            if (!sourceMapsByBinding.TryGetValue(binding.MapId, out var matches))
            {
                matches = [];
                sourceMapsByBinding.Add(binding.MapId, matches);
            }

            matches.Add(sourceMap);
        }

        var matchedSourceMapCount = sourceMapsByBinding.Values.Sum(values => values.Count);
        if (sourceMaps.Length == 0 ||
            matchedSourceMapCount * 2 < sourceMaps.Length ||
            sourceMapsByBinding.Count * 2 < bindingDrafts.Count)
        {
            throw Refused(
                "source.map-coverage-insufficient",
                "Fewer than half of the source maps or supported catalog maps match reviewed aliases; " +
                "an empty or grossly partial first publication was refused.");
        }

        var itemCache = new Dictionary<string, LootSpawnCandidate>(StringComparer.Ordinal);
        var snapshots = new List<LootSpawnSnapshot>(bindingDrafts.Count);
        var coverageEntries = new List<LootSpawnMapSourceCoverage>(bindingDrafts.Count);
        var diagnostics = new List<LootSpawnSourceDiagnostic>();
        var bindings = new List<TarkovDevLootSpawnMapBinding>(bindingDrafts.Count);
        long bundleCandidateCount = 0;
        foreach (var binding in bindingDrafts.OrderBy(value => value.MapId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceMapsByBinding.TryGetValue(binding.MapId, out var matchingMaps);
            matchingMaps ??= [];
            var mapResult = NormalizeMap(
                binding,
                matchingMaps,
                request,
                datasetVersion,
                sourceFreshness,
                itemFreshness,
                sourceIdentifier,
                sourceReference,
                itemIdentifier,
                itemReference,
                dataThroughUtc,
                confidence,
                itemCache,
                ref bundleCandidateCount,
                cancellationToken);
            snapshots.Add(mapResult.Snapshot);
            coverageEntries.Add(mapResult.Coverage);
            diagnostics.AddRange(mapResult.Diagnostics);
            bindings.Add(new(
                binding.MapId,
                binding.Variant.Key,
                binding.TransformVersion,
                binding.SceneBounds,
                matchingMaps.Select(map => map.NormalizedName ?? map.Id).ToArray(),
                binding.Variant.Floors.Select(floor => floor.Id).ToArray(),
                request.MapCatalog.Provenance));
        }

        if (unmatchedSourceMaps > 0)
        {
            diagnostics.Add(new(
                "source.maps-unmatched",
                $"{unmatchedSourceMaps.ToString(CultureInfo.InvariantCulture)} source map record(s) do not match " +
                "a supported reviewed map-catalog alias and were not guessed."));
        }

        if (request.MapCatalog.SkippedLocations.Count > 0)
        {
            diagnostics.Add(new(
                "map-catalog.locations-skipped",
                "The reviewed map catalog skipped " +
                $"{request.MapCatalog.SkippedLocations.Count.ToString(CultureInfo.InvariantCulture)} malformed location(s); " +
                "no aliases or transforms were invented for them."));
        }

        if (diagnostics.Count > LootSpawnSourceBundle.MaximumDiagnostics)
        {
            throw Refused("source.diagnostics-oversized", "Normalization produced too many bounded diagnostics.");
        }

        var identity = new LootSpawnSourceIdentity(
            1,
            datasetVersion,
            contentHash,
            request.ImportedUtc,
            dataThroughUtc,
            request.ImportedUtc,
            EvidenceSourceClass.PublicStructuredData,
            sourceIdentifier,
            sourceReference,
            SourceTerms,
            confidence,
            Producer,
            [
                new("maps-source", sourceIdentifier, HashText(request.Maps.RawSourceJson!, cancellationToken)),
                new("maps-language-view", $"{sourceIdentifier}#{request.Language.Trim().ToLowerInvariant()}",
                    HashText(request.Maps.Json, cancellationToken)),
                new("items-source", itemIdentifier, HashText(request.Items.RawSourceJson!, cancellationToken)),
                new("items-language-view", $"{itemIdentifier}#{request.Language.Trim().ToLowerInvariant()}",
                    HashText(request.Items.Json, cancellationToken)),
                new(
                    "map-catalog",
                    request.MapCatalog.Provenance.SourceUri.AbsoluteUri,
                    request.MapCatalog.Provenance.ContentSha256),
            ]);
        return new(
            new LootSpawnSourceBundle(
                identity,
                snapshots,
                coverageEntries,
                diagnostics.OrderBy(value => value.MapId, StringComparer.Ordinal)
                    .ThenBy(value => value.Code, StringComparer.Ordinal)
                    .ToArray()),
            Array.AsReadOnly(bindings.OrderBy(value => value.MapId, StringComparer.Ordinal).ToArray()));
    }

    private static MapNormalizationResult NormalizeMap(
        BindingDraft binding,
        IReadOnlyList<TarkovDevMap> sourceMaps,
        TarkovDevLootSpawnNormalizationRequest request,
        string datasetVersion,
        FreshnessState sourceFreshness,
        FreshnessState itemFreshness,
        string sourceIdentifier,
        string sourceReference,
        string itemIdentifier,
        string itemReference,
        DateTimeOffset dataThroughUtc,
        EvidenceConfidence confidence,
        Dictionary<string, LootSpawnCandidate> itemCache,
        ref long bundleCandidateCount,
        CancellationToken cancellationToken)
    {
        var known = sourceMaps.Sum(map => checked(map.LootContainers.Count + map.LootLoose.Count));
        var records = new List<LootSpawnRecord>(sourceMaps.Sum(map => map.LootLoose.Count));
        var omittedPools = 0;
        foreach (var sourceMap in sourceMaps.OrderBy(map => map.Id, StringComparer.Ordinal))
        {
            for (var ordinal = 0; ordinal < sourceMap.LootLoose.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sourceMap.LootLoose[ordinal];
                if (!TryCandidates(
                        source.Items,
                        request,
                        itemFreshness,
                        itemIdentifier,
                        itemReference,
                        itemCache,
                        out var candidates))
                {
                    omittedPools++;
                    continue;
                }

                bundleCandidateCount += candidates.Count;
                if (bundleCandidateCount > MaximumBundleCandidates)
                {
                    throw Refused(
                        "source.aggregate-budget-exceeded",
                        "The normalized production source exceeds the bundle candidate budget.");
                }

                if (records.Count >= LootSpawnSnapshot.MaximumRecords)
                {
                    throw Refused(
                        "source.aggregate-budget-exceeded",
                        $"Map '{binding.MapId}' exceeds the bounded snapshot record budget.");
                }

                var world = World(source.Position);
                var points = TryProject(binding, world, out var projected)
                    ? new[] { new MapScenePoint(projected.X, projected.Y) }
                    : null;
                var floors = points is null || world is null
                    ? []
                    : ResolveFloors(binding.Variant, world.Value);
                var location = points is null
                    ? new LootSpawnLocation(LootSpawnPrecision.MapOnly, null)
                    : new LootSpawnLocation(LootSpawnPrecision.ExactPoint, points, floors);
                var provenance = SourceProvenance(
                    request,
                    sourceIdentifier,
                    sourceReference,
                    dataThroughUtc,
                    confidence,
                    1,
                    1);
                var unknown = new ResultStatus(
                    ResultCompleteness.Unknown,
                    sourceFreshness,
                    "source.not-published");
                records.Add(new(
                    StableSpawnId(sourceMap.Id, source.Position, source.Items),
                    binding.MapId,
                    candidates.Count == 1 ? candidates[0].DisplayName : "Potential loose loot",
                    location,
                    candidates.Count == 1
                        ? LootSpawnPoolKind.SingleKnownItem
                        : LootSpawnPoolKind.UnweightedCandidates,
                    candidates,
                    new EvidencedValue<double?>("spawn-probability", null, unknown, provenance),
                    new EvidencedValue<string?>("respawn-behavior", null, unknown, provenance),
                    datasetVersion,
                    binding.TransformVersion,
                    new ResultStatus(
                        ResultCompleteness.Complete,
                        sourceFreshness,
                        sourceFreshness == FreshnessState.Stale
                            ? "loot-spawn-source.stale"
                            : "loot-spawn-source.current"),
                    provenance));
            }
        }

        var published = records.Count;
        var positioned = records.Count(record => record.Location.Geometry is not null);
        var floorResolved = records.Count(record =>
            record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        var unresolved = published - positioned;
        var coverage = new LootSpawnMapSourceCoverage(
            binding.MapId,
            known,
            published,
            positioned,
            floorResolved,
            unresolved);
        var completeness = published < known ? ResultCompleteness.Partial : ResultCompleteness.Complete;
        var snapshotProvenance = SourceProvenance(
            request,
            sourceIdentifier,
            sourceReference,
            dataThroughUtc,
            confidence,
            known,
            published);
        var snapshot = new LootSpawnSnapshot(
            StableSnapshotId(datasetVersion, binding.MapId),
            datasetVersion,
            binding.MapId,
            binding.TransformVersion,
            request.ImportedUtc,
            new ResultStatus(
                completeness,
                sourceFreshness,
                completeness == ResultCompleteness.Partial
                    ? "loot-spawn-source.partial"
                    : "loot-spawn-source.current"),
            new LootSpawnCoverage(published, positioned, floorResolved, unresolved),
            snapshotProvenance,
            records);
        var diagnostics = new List<LootSpawnSourceDiagnostic>(2);
        if (published < known)
        {
            var containerCount = sourceMaps.Sum(map => map.LootContainers.Count);
            diagnostics.Add(new(
                "coverage.map-partial",
                $"Published {published.ToString(CultureInfo.InvariantCulture)} of " +
                $"{known.ToString(CultureInfo.InvariantCulture)} known source records. " +
                $"{containerCount.ToString(CultureInfo.InvariantCulture)} container record(s) have no published candidate contents and " +
                $"{omittedPools.ToString(CultureInfo.InvariantCulture)} loose pool(s) were invalid or unresolved.",
                binding.MapId));
        }

        if (unresolved > 0 || positioned > floorResolved && binding.Variant.Floors.Count > 1)
        {
            diagnostics.Add(new(
                "coverage.map-unresolved",
                $"{unresolved.ToString(CultureInfo.InvariantCulture)} published record(s) are map-only and " +
                $"{(positioned - floorResolved).ToString(CultureInfo.InvariantCulture)} positioned record(s) " +
                "have no explicit floor-layer match.",
                binding.MapId));
        }

        return new(snapshot, coverage, diagnostics);
    }

    private static bool TryCandidates(
        IReadOnlyList<string>? sourceIds,
        TarkovDevLootSpawnNormalizationRequest request,
        FreshnessState freshness,
        string sourceIdentifier,
        string sourceReference,
        Dictionary<string, LootSpawnCandidate> cache,
        out IReadOnlyList<LootSpawnCandidate> candidates)
    {
        candidates = [];
        if (sourceIds is null || sourceIds.Count is < 1 or > LootSpawnRecord.MaximumCandidates)
        {
            return false;
        }

        var ids = sourceIds.Take(LootSpawnRecord.MaximumCandidates + 1).ToArray();
        if (ids.Length != sourceIds.Count ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => CanonicalOptional(id, 256) is null))
        {
            return false;
        }

        var resolved = new List<LootSpawnCandidate>(ids.Length);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            if (!request.Items.Data.Items.TryGetValue(id, out var item) ||
                !string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return false;
            }

            if (!cache.TryGetValue(id, out var candidate))
            {
                candidate = Candidate(item, request, freshness, sourceIdentifier, sourceReference);
                cache.Add(id, candidate);
            }

            resolved.Add(candidate);
        }

        candidates = resolved;
        return true;
    }

    private static LootSpawnCandidate Candidate(
        TarkovDevItem item,
        TarkovDevLootSpawnNormalizationRequest request,
        FreshnessState freshness,
        string sourceIdentifier,
        string sourceReference)
    {
        var provenance = ItemProvenance(request, item, sourceIdentifier, sourceReference);
        var fleaGross = item.Types.Contains("noFlea", StringComparer.OrdinalIgnoreCase)
            ? null
            : item.LastLowPrice;
        var trader = item.SellToTrader
            .Select(offer => offer.PriceRub is > 0
                ? (long?)offer.PriceRub.GetValueOrDefault()
                : string.Equals(offer.Currency, "RUB", StringComparison.OrdinalIgnoreCase) && offer.Price is > 0
                    ? (long?)offer.Price.GetValueOrDefault()
                    : null)
            .OfType<long>()
            .DefaultIfEmpty()
            .Max();
        long? bestTrader = trader > 0 ? trader : null;
        var squares = checked(item.Width * item.Height);
        return new(
            item.Id,
            DisplayName(item),
            Category(item, request.Items.Data.ItemCategories),
            ItemValue("flea-gross", fleaGross, provenance, freshness),
            ItemValue<long>("flea-net", null, provenance, freshness),
            ItemValue("best-trader", bestTrader, provenance, freshness),
            ItemValue<int>("occupied-squares", squares, provenance, freshness));
    }

    private static EvidencedValue<T?> ItemValue<T>(
        string fieldId,
        T? value,
        EvidenceProvenance provenance,
        FreshnessState freshness)
        where T : struct => new(
        fieldId,
        value,
        new ResultStatus(
            value is null ? ResultCompleteness.Unknown : ResultCompleteness.Complete,
            freshness,
            value is null ? "item.value-unavailable" : "item.value-published"),
        provenance);

    private static EvidenceProvenance ItemProvenance(
        TarkovDevLootSpawnNormalizationRequest request,
        TarkovDevItem item,
        string sourceIdentifier,
        string sourceReference)
    {
        var observed = request.Items.CachedUtc.ToUniversalTime();
        var itemThrough = item.Updated?.ToUniversalTime();
        var dataThrough = itemThrough is { } candidate && candidate <= observed
            ? candidate
            : DataThrough(request.Items);
        if (dataThrough > observed)
        {
            dataThrough = observed;
        }

        return new(
            EvidenceSourceClass.PublicStructuredData,
            sourceIdentifier,
            observed,
            EvidenceConfidence.Unscored,
            Producer,
            dataThroughUtc: dataThrough,
            reference: sourceReference);
    }

    private static EvidenceProvenance SourceProvenance(
        TarkovDevLootSpawnNormalizationRequest request,
        string sourceIdentifier,
        string sourceReference,
        DateTimeOffset dataThroughUtc,
        EvidenceConfidence confidence,
        int known,
        int published) => new(
        EvidenceSourceClass.PublicStructuredData,
        sourceIdentifier,
        request.ImportedUtc,
        confidence,
        Producer,
        dataThroughUtc,
        request.ImportedUtc,
        new EvidenceCoverage(
            published,
            known == 0 ? null : (double)published / known,
            $"{published.ToString(CultureInfo.InvariantCulture)} of {known.ToString(CultureInfo.InvariantCulture)} known records"),
        sourceReference);

    private static IReadOnlyList<BindingDraft> BuildBindings(
        TarkovDevMapCatalog catalog,
        CancellationToken cancellationToken)
    {
        var bindings = new List<BindingDraft>();
        foreach (var location in catalog.Locations.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!location.Variants.Any(variant => variant.HasRuntimeAsset))
            {
                continue;
            }

            var mapId = CanonicalOptional(location.Id, 128)
                ?? throw Refused("map-catalog.identity-invalid", "A supported map has a non-canonical location identity.");
            var variants = location.Variants.Take(MaximumVariantsPerLocation + 1).ToArray();
            if (variants.Length > MaximumVariantsPerLocation)
            {
                throw Refused("map-catalog.variants-oversized", $"Map '{mapId}' has too many variants.");
            }

            var variant = variants
                .Where(value => value.HasRuntimeAsset)
                .OrderByDescending(value => value.IsInteractive &&
                                            value.Transform?.IsValid == true &&
                                            value.Bounds?.IsValid == true)
                .ThenByDescending(value => value.IsInteractive)
                .ThenBy(value => value.Key, StringComparer.Ordinal)
                .First();
            _ = CanonicalOptional(variant.Key, 128)
                ?? throw Refused("map-catalog.identity-invalid", $"Map '{mapId}' has a non-canonical variant identity.");
            var rawAliases = new[] { location.Id, location.SourceId }
                .Concat(variants.SelectMany(value => value.AlternateLocationIds))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(33)
                .ToArray();
            if (rawAliases.Length > 32)
            {
                throw Refused("map-catalog.layers-oversized", $"Map '{mapId}' has too many aliases.");
            }

            var aliases = rawAliases
                .Select(value => CanonicalOptional(value, 128)
                    ?? throw Refused("map-catalog.alias-invalid", $"Map '{mapId}' has a non-canonical alias."))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (variant.Floors.Count > LootSpawnLocation.MaximumFloors ||
                variant.Floors.Select(floor => floor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variant.Floors.Count ||
                variant.Floors.Any(floor => CanonicalOptional(floor.Id, 96) is null))
            {
                throw Refused("map-catalog.layers-oversized", $"Map '{mapId}' has invalid aliases or floor identities.");
            }

            var sceneBounds = ProjectedBounds(variant);
            var transformVersion = TransformVersion(mapId, variant);
            bindings.Add(new(mapId, variant, aliases, transformVersion, sceneBounds));
        }

        if (bindings.Count is < 1 or > LootSpawnSourceImportContext.MaximumMaps)
        {
            throw Refused("map-catalog.coverage-invalid", "The map catalog has no supported maps or exceeds the supported-map bound.");
        }

        if (bindings.Select(value => value.MapId).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
        {
            throw Refused("map-catalog.identity-duplicate", "Supported map identities are not unique.");
        }

        return bindings;
    }

    private static IReadOnlyDictionary<string, BindingDraft> BuildAliasIndex(IReadOnlyList<BindingDraft> bindings)
    {
        var aliases = new Dictionary<string, BindingDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            foreach (var alias in binding.Aliases)
            {
                if (aliases.TryGetValue(alias, out var existing) &&
                    !string.Equals(existing.MapId, binding.MapId, StringComparison.Ordinal))
                {
                    throw Refused(
                        "map-catalog.alias-ambiguous",
                        $"Map alias '{alias}' resolves to more than one supported location.");
                }

                aliases[alias] = binding;
            }
        }

        return aliases;
    }

    private static MapSceneBounds? ProjectedBounds(MapVariant variant)
    {
        if (variant.Transform is not { IsValid: true } transform || variant.Bounds?.IsValid != true)
        {
            return null;
        }

        var bounds = variant.Bounds;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = new MapPoint[corners.Length];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!transform.TryProject(corners[index], out projected[index]))
            {
                return null;
            }
        }

        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        return maximumX > minimumX && maximumY > minimumY
            ? new MapSceneBounds(minimumX, minimumY, maximumX, maximumY)
            : null;
    }

    private static bool TryProject(BindingDraft binding, WorldPosition? world, out MapPoint projected)
    {
        projected = default;
        return world is { } position &&
               binding.Variant.Bounds?.Contains(position.X, position.Z) == true &&
               binding.Variant.Transform?.TryProject(position, out projected) == true &&
               binding.SceneBounds?.Contains(new MapScenePoint(projected.X, projected.Y)) == true;
    }

    private static IReadOnlyList<string> ResolveFloors(MapVariant variant, WorldPosition world) =>
        variant.Floors
            .Where(floor => floor.Extents.Count > 0 &&
                            (!string.Equals(floor.Id, "base", StringComparison.OrdinalIgnoreCase) ||
                             floor.Extents.Any(extent =>
                                 extent.MinimumHeight is not null || extent.MaximumHeight is not null)) &&
                            floor.Extents.Any(extent => extent.Contains(world)))
            .Select(floor => floor.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(LootSpawnLocation.MaximumFloors)
            .ToArray();

    private static WorldPosition? World(TarkovDevMapPosition? position) =>
        position is { X: { } x, Y: { } y, Z: { } z } &&
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
            ? new WorldPosition(x, y, z)
            : null;

    private static string StableSpawnId(
        string sourceMapId,
        TarkovDevMapPosition? position,
        IReadOnlyList<string> candidateIds)
    {
        var coordinate = position is { X: { } x, Y: { } y, Z: { } z }
            ? string.Join(',',
                x.ToString("R", CultureInfo.InvariantCulture),
                y.ToString("R", CultureInfo.InvariantCulture),
                z.ToString("R", CultureInfo.InvariantCulture))
            : string.Join(',', candidateIds.Order(StringComparer.Ordinal));
        return $"json-tarkov-dev:{HashText($"{sourceMapId}|{coordinate}")}";
    }

    private static string StableSnapshotId(string datasetVersion, string mapId) =>
        $"json-tarkov-dev-snapshot:{HashText($"{datasetVersion}|{mapId}")}";

    private static string TransformVersion(string mapId, MapVariant variant) =>
        LootSpawnTransformIdentity.For(mapId, variant);

    private static string CompositeContentHash(
        TarkovDevLootSpawnNormalizationRequest request,
        string mode,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, "tarkov-companion-loot-spawn-composite-v1", cancellationToken);
        AppendUtf8(hash, mode, cancellationToken);
        AppendUtf8(hash, request.Language.Trim().ToLowerInvariant(), cancellationToken);
        AppendUtf8(hash, request.Maps.RawSourceJson!, cancellationToken);
        AppendUtf8(hash, request.Maps.Json, cancellationToken);
        AppendUtf8(hash, request.Items.RawSourceJson!, cancellationToken);
        AppendUtf8(hash, request.Items.Json, cancellationToken);
        AppendUtf8(hash, request.MapCatalog.Provenance.SourceUri.AbsoluteUri, cancellationToken);
        AppendUtf8(hash, request.MapCatalog.Provenance.ContentSha256, cancellationToken);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendUtf8(
        IncrementalHash hash,
        string value,
        CancellationToken cancellationToken,
        bool appendTerminator = true)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var remaining = value.AsSpan();
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoder.Convert(
                    remaining,
                    rented,
                    true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                hash.AppendData(rented.AsSpan(0, bytesUsed));
                remaining = remaining[charsUsed..];
            }

            if (appendTerminator)
            {
                hash.AppendData([0]);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string HashText(string value, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, value, cancellationToken, appendTerminator: false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static DateTimeOffset DataThrough<T>(TarkovDevResponse<T> response)
    {
        var cached = response.CachedUtc.ToUniversalTime();
        var modified = response.LastModified?.ToUniversalTime();
        return modified is { } value && value <= cached ? value : cached;
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    private static void ValidateRequest(TarkovDevLootSpawnNormalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Maps);
        ArgumentNullException.ThrowIfNull(request.Items);
        ArgumentNullException.ThrowIfNull(request.MapCatalog);
        if (request.ImportedUtc == default || request.ImportedUtc.Offset != TimeSpan.Zero)
        {
            throw Refused("source.import-time-invalid", "A non-default UTC normalization time is required.");
        }

        var language = request.Language?.Trim().ToLowerInvariant();
        if (language is null || language.Length is < 2 or > 8 ||
            language.Any(character => !char.IsAsciiLetter(character) && character != '-'))
        {
            throw Refused("source.language-invalid", "A bounded BCP-47-style language is required.");
        }

        var mode = ModeSlug(request.GameMode);
        if (!string.Equals(request.Maps.SourceKey, $"{mode}/maps", StringComparison.Ordinal) ||
            !string.Equals(request.Items.SourceKey, $"{mode}/items", StringComparison.Ordinal))
        {
            throw Refused(
                "source.mode-mismatch",
                "Maps and items must carry exact source keys for the requested json.tarkov.dev game mode.");
        }

        // [#799] A cached-at or retrieved-at stamp later than the import time is not refused: all
        // three are this PC's clock, and one that jumped back after the responses were cached
        // refused every refresh for as long as the clock had been ahead.
        if (request.Maps.CachedUtc == default || request.Items.CachedUtc == default ||
            request.Maps.CachedUtc.Offset != TimeSpan.Zero || request.Items.CachedUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(request.Maps.Json) || string.IsNullOrWhiteSpace(request.Items.Json) ||
            string.IsNullOrWhiteSpace(request.Maps.RawSourceJson) ||
            string.IsNullOrWhiteSpace(request.Items.RawSourceJson) ||
            string.IsNullOrWhiteSpace(request.MapCatalogJson))
        {
            throw Refused("source.response-metadata-invalid", "Source response identity or observation time is invalid.");
        }

        var provenance = request.MapCatalog.Provenance;
        if (provenance is null || provenance.SourceUri is null ||
            provenance.RetrievedUtc == default || provenance.RetrievedUtc.Offset != TimeSpan.Zero ||
            provenance.SourceUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(provenance.SourceUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !provenance.SourceUri.AbsolutePath.StartsWith("/the-hideout/tarkov-dev/", StringComparison.Ordinal) ||
            !provenance.SourceUri.AbsolutePath.EndsWith(MapCatalogPathSuffix, StringComparison.Ordinal) ||
            provenance.ContentSha256 is not { Length: 64 } contentSha256 ||
            contentSha256.Any(value => !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) ||
            provenance.Availability == MapCatalogAvailability.Unavailable)
        {
            throw Refused("map-catalog.provenance-invalid", "The map catalog lacks exact reviewed source provenance.");
        }
    }

    private static TarkovDevLootSpawnNormalizationRequest BindExactDocuments(
        TarkovDevLootSpawnNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateExactDocument(request.Maps.RawSourceJson!, request.Maps.SourceKey! + "#raw", cancellationToken);
        ValidateExactDocument(request.Items.RawSourceJson!, request.Items.SourceKey! + "#raw", cancellationToken);
        return request with
        {
            Maps = request.Maps with
            {
                Data = ReadExactData<TarkovDevMapsData>(request.Maps.Json, request.Maps.SourceKey!, cancellationToken),
            },
            Items = request.Items with
            {
                Data = ReadExactData<TarkovDevItemsData>(request.Items.Json, request.Items.SourceKey!, cancellationToken),
            },
            MapCatalog = ReadExactMapCatalog(request, cancellationToken),
        };
    }

    private static TarkovDevMapCatalog ReadExactMapCatalog(
        TarkovDevLootSpawnNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(request.MapCatalogJson) > MaximumMapCatalogBytes)
        {
            throw Refused("map-catalog.response-oversized", "The reviewed map catalog exceeds its normalization byte budget.");
        }

        using (var document = JsonDocument.Parse(request.MapCatalogJson, new JsonDocumentOptions
               {
                   AllowTrailingCommas = false,
                   CommentHandling = JsonCommentHandling.Disallow,
                   MaxDepth = 64,
               }))
        {
            ValidateUniqueProperties(document.RootElement, cancellationToken);
        }

        var provenance = request.MapCatalog.Provenance;
        var parsed = TarkovDevMapCatalogParser.Parse(
            request.MapCatalogJson,
            provenance.SourceUri,
            provenance.RetrievedUtc,
            provenance.Availability);
        if (!string.Equals(
                parsed.Provenance.ContentSha256,
                provenance.ContentSha256,
                StringComparison.Ordinal))
        {
            throw Refused("map-catalog.content-mismatch", "The map catalog object does not match its exact source document.");
        }

        return parsed;
    }

    private static T ReadExactData<T>(string json, string sourceKey, CancellationToken cancellationToken)
        where T : class
    {
        ValidateExactDocument(json, sourceKey, cancellationToken);
        var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<T>>(json, SourceJsonOptions)
            ?? throw Refused("source.response-invalid", $"Response '{sourceKey}' has no typed envelope.");
        return envelope.Data ?? throw Refused("source.response-invalid", $"Response '{sourceKey}' has a null data envelope.");
    }

    private static void ValidateExactDocument(
        string json,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
        {
            throw Refused("source.response-oversized", $"Response '{sourceKey}' exceeds the normalization byte budget.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        ValidateUniqueProperties(document.RootElement, cancellationToken);
    }

    private static void ValidateUniqueProperties(JsonElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Web-default deserialization matches CLR properties case-insensitively, so two JSON
            // spellings that differ only by case are still competing assignments to one field.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Refused("source.duplicate-property", $"Source JSON repeats property '{property.Name}'.");
                }

                ValidateUniqueProperties(property.Value, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                ValidateUniqueProperties(value, cancellationToken);
            }
        }
    }

    private static void ValidateTopLevelBudgets(TarkovDevLootSpawnNormalizationRequest request)
    {
        if (request.Maps.Data.Maps is null || request.Items.Data.Items is null)
        {
            throw Refused("source.response-invalid", "A production response has a null top-level collection.");
        }

        if (request.Maps.Data.Maps.Count > MaximumSourceMaps ||
            request.Items.Data.Items.Count > LootSpawnSourceImportContext.MaximumCatalogItems ||
            request.MapCatalog.Locations.Count > LootSpawnSourceImportContext.MaximumMaps ||
            request.Maps.Data.Maps.Take(MaximumSourceMaps + 1).Count() > MaximumSourceMaps ||
            request.Items.Data.Items.Take(LootSpawnSourceImportContext.MaximumCatalogItems + 1).Count() >
                LootSpawnSourceImportContext.MaximumCatalogItems ||
            request.MapCatalog.Locations.Take(LootSpawnSourceImportContext.MaximumMaps + 1).Count() >
                LootSpawnSourceImportContext.MaximumMaps)
        {
            throw Refused("source.collection-oversized", "A production source collection exceeds its top-level bound.");
        }
    }

    private static void ValidateAggregateBudgets(
        TarkovDevLootSpawnNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        long knownRecords = 0;
        long candidates = 0;
        foreach (var map in request.Maps.Data.Maps.Values.Take(MaximumSourceMaps + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (map is null || map.LootLoose is null || map.LootContainers is null)
            {
                throw Refused("source.response-invalid", "A production map has a null bounded collection.");
            }

            if (map.LootLoose.Count > LootSpawnSnapshot.MaximumRecords ||
                map.LootLoose.Take(LootSpawnSnapshot.MaximumRecords + 1).Count() > LootSpawnSnapshot.MaximumRecords)
            {
                throw Refused("source.collection-oversized", $"Map '{map.Id}' exceeds the loose-loot record bound.");
            }

            knownRecords += (long)map.LootContainers.Count + map.LootLoose.Count;
            if (knownRecords > MaximumKnownRecords)
            {
                throw Refused("source.aggregate-budget-exceeded", "The production maps response exceeds the known-record budget.");
            }

            foreach (var loose in map.LootLoose.Take(LootSpawnSnapshot.MaximumRecords + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (loose is null || loose.Items is null)
                {
                    throw Refused("source.response-invalid", "A production loose-loot record has a null item pool.");
                }

                if (loose.Items.Count > LootSpawnRecord.MaximumCandidates)
                {
                    throw Refused("source.collection-oversized", "A loose-loot candidate pool exceeds its bound.");
                }

                candidates += loose.Items.Count;
                if (candidates > MaximumBundleCandidates)
                {
                    throw Refused("source.aggregate-budget-exceeded", "The production maps response exceeds the candidate budget.");
                }
            }
        }
    }

    private static string DisplayName(TarkovDevItem item)
    {
        foreach (var value in new[] { item.Name, item.ShortName, Humanize(item.NormalizedName), item.Id })
        {
            if (CanonicalOptional(value, 256) is { } candidate &&
                !string.Equals(candidate, $"{item.Id} Name", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, $"{item.Id} ShortName", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return item.Id;
    }

    private static string Category(
        TarkovDevItem item,
        IReadOnlyDictionary<string, TarkovDevItemCategory> categories)
    {
        foreach (var type in item.Types)
        {
            var known = type.ToLowerInvariant() switch
            {
                "ammo" => "Ammunition",
                "ammobox" => "Ammunition pack",
                "keys" => "Key",
                "provisions" => "Provision",
                "meds" or "injectors" => "Medicine",
                "gun" => "Weapon",
                "mods" => "Attachment",
                "armor" => "Armor",
                "armorplate" => "Armor plate",
                "helmet" => "Helmet",
                "headphones" => "Headset",
                "rig" => "Rig",
                "backpack" => "Backpack",
                "container" => "Container",
                "barter" => "Barter",
                _ => null,
            };
            if (known is not null)
            {
                return known;
            }
        }

        foreach (var categoryId in item.Categories)
        {
            if (categories.TryGetValue(categoryId, out var category) &&
                CanonicalOptional(category.Name, 128) is { } name &&
                !string.Equals(name, $"{category.Id} Name", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return "Unknown";
    }

    private static string? Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var words = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? null : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? CanonicalOptional(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.IsNormalized(NormalizationForm.FormC) &&
        !value.Any(char.IsControl)
            ? value
            : null;

    private static string ModeSlug(GameMode mode) => mode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw Refused("source.mode-invalid", "The requested game mode is not supported."),
    };

    private static LootSpawnSourceImportException Refused(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private sealed record BindingDraft(
        string MapId,
        MapVariant Variant,
        IReadOnlyList<string> Aliases,
        string TransformVersion,
        MapSceneBounds? SceneBounds);

    private sealed record MapNormalizationResult(
        LootSpawnSnapshot Snapshot,
        LootSpawnMapSourceCoverage Coverage,
        IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics);
}
