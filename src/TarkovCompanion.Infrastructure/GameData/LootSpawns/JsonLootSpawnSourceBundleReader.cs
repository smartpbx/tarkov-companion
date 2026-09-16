using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

/// <summary>
/// Reads normalized location bundles without treating item metadata as a location source.
/// </summary>
/// <remarks>
/// json.tarkov.dev maps publish loose-loot positions and candidate item IDs, while the item catalog
/// supplies names, categories, footprints and prices. The map feed does not publish explicit floor,
/// probability, or respawn facts, so those stay unknown unless a separately reviewed curated source
/// owns them. Every normalized bundle names its location provenance and licence; this reader never
/// derives coordinates from item metadata or fills one map from another.
/// </remarks>
public sealed class JsonLootSpawnSourceBundleReader : ILootSpawnSourceBundleReader
{
    public const int SupportedSchemaVersion = 1;
    public const int MaximumManifestBytes = 64 * 1024;
    public const int MaximumContentBytes = 4 * 1024 * 1024;
    public const int MaximumJsonDepth = 32;
    public const int MaximumMaps = 64;
    public const int MaximumStringLength = 1024;
    public const int MaximumBundleRecords = 32_768;
    public const int MaximumBundleCandidates = 65_536;
    public const int MaximumBundleGeometryPoints = 262_144;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
    };

    public ValueTask<LootSpawnSourceBundle> ReadAsync(
        LootSpawnSourceDocuments documents,
        LootSpawnSourceImportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ValueTask.FromResult(Read(documents, context, cancellationToken));
        }
        catch (LootSpawnSourceImportException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Refused("source.json-invalid", "The loot-spawn source is not valid bounded JSON.", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            throw Refused("source.validation-failed", "The loot-spawn source violates the import contract.", exception);
        }
    }

    private static LootSpawnSourceBundle Read(
        LootSpawnSourceDocuments documents,
        LootSpawnSourceImportContext context,
        CancellationToken cancellationToken)
    {
        BoundDocument(documents.Manifest, MaximumManifestBytes, "manifest");
        BoundDocument(documents.Content, MaximumContentBytes, "content");

        using var manifestDocument = JsonDocument.Parse(documents.Manifest, DocumentOptions);
        ValidateJsonShape(manifestDocument.RootElement, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = ReadManifest(manifestDocument.RootElement, context);

        var actualHash = Convert.ToHexString(SHA256.HashData(documents.Content.Span)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(manifest.ContentSha256),
                Convert.FromHexString(actualHash)))
        {
            throw Refused("content.hash-mismatch", "The content SHA-256 does not match the versioned manifest.");
        }

        using var contentDocument = JsonDocument.Parse(documents.Content, DocumentOptions);
        ValidateJsonShape(contentDocument.RootElement, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var content = RequireObject(contentDocument.RootElement, "content");
        RequireSchema(content);
        var contentVersion = RequiredString(content, "datasetVersion", 128);
        if (!string.Equals(contentVersion, manifest.DatasetVersion, StringComparison.Ordinal))
        {
            throw Refused("content.identity-mismatch", "The content dataset version does not match its manifest.");
        }

        var snapshotElements = RequiredArray(content, "snapshots", MaximumMaps);
        var snapshots = new List<LootSpawnSnapshot>(snapshotElements.GetArrayLength());
        var seenMaps = new HashSet<string>(StringComparer.Ordinal);
        var seenSnapshots = new HashSet<string>(StringComparer.Ordinal);
        long bundleRecordCount = 0;
        long bundleCandidateCount = 0;
        long bundleGeometryPointCount = 0;
        foreach (var snapshotElement in snapshotElements.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotObject = RequireObject(snapshotElement, "snapshot");
            var snapshotId = RequiredString(snapshotObject, "snapshotId", 160);
            var mapId = RequiredString(snapshotObject, "mapId", 128);
            var transformVersion = RequiredString(snapshotObject, "transformVersion", 128);
            if (!seenSnapshots.Add(snapshotId))
            {
                throw Refused("snapshot.id-duplicate", $"Duplicate snapshot id '{snapshotId}'.");
            }

            if (!seenMaps.Add(mapId))
            {
                throw Refused("snapshot.map-duplicate", $"Map '{mapId}' has more than one snapshot.");
            }

            if (!context.Maps.TryGetValue(mapId, out var map))
            {
                throw Refused("snapshot.map-unknown", $"Map '{mapId}' is absent from the reviewed map catalog.");
            }

            if (!string.Equals(transformVersion, map.TransformVersion, StringComparison.Ordinal))
            {
                throw Refused("snapshot.transform-incompatible", $"Map '{mapId}' uses an incompatible transform version.");
            }

            var recordsElement = RequiredArray(snapshotObject, "records", LootSpawnSnapshot.MaximumRecords);
            var records = new List<LootSpawnRecord>(recordsElement.GetArrayLength());
            var seenRecords = new HashSet<string>(StringComparer.Ordinal);
            long snapshotCandidateCount = 0;
            long snapshotGeometryPointCount = 0;
            foreach (var recordElement in recordsElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ReadRecord(
                    RequireObject(recordElement, "record"),
                    map,
                    manifest,
                    context,
                    cancellationToken);
                if (!seenRecords.Add(record.SpawnId))
                {
                    throw Refused("record.id-duplicate", $"Map '{mapId}' repeats spawn id '{record.SpawnId}'.");
                }

                snapshotCandidateCount += record.Candidates.Count;
                snapshotGeometryPointCount += record.Location.GeometryPoints?.Count ?? 0;
                bundleRecordCount++;
                bundleCandidateCount += record.Candidates.Count;
                bundleGeometryPointCount += record.Location.GeometryPoints?.Count ?? 0;
                if (snapshotCandidateCount > LootSpawnSnapshot.MaximumTotalCandidates ||
                    snapshotGeometryPointCount > LootSpawnSnapshot.MaximumTotalGeometryPoints ||
                    bundleRecordCount > MaximumBundleRecords ||
                    bundleCandidateCount > MaximumBundleCandidates ||
                    bundleGeometryPointCount > MaximumBundleGeometryPoints)
                {
                    throw Refused(
                        "source.aggregate-budget-exceeded",
                        "The loot-spawn source exceeds its snapshot or bundle record, candidate, or geometry budget.");
                }

                records.Add(record);
            }

            var coverage = manifest.Coverage.SingleOrDefault(value =>
                string.Equals(value.MapId, mapId, StringComparison.Ordinal))
                ?? throw Refused("coverage.map-missing-declaration", $"Map '{mapId}' has no manifest coverage declaration.");
            ValidateMeasuredCoverage(coverage, records);
            var provenance = Provenance(manifest, context.ImportedUtc, coverage);
            var completeness = coverage.PublishedRecordCount < coverage.KnownRecordCount
                ? ResultCompleteness.Partial
                : ResultCompleteness.Complete;
            snapshots.Add(new(
                snapshotId,
                manifest.DatasetVersion,
                mapId,
                transformVersion,
                context.ImportedUtc,
                new ResultStatus(completeness, FreshnessState.Current, completeness == ResultCompleteness.Partial
                    ? "loot-spawn-source.partial"
                    : "loot-spawn-source.current"),
                new LootSpawnCoverage(
                    coverage.PublishedRecordCount,
                    coverage.PositionedRecordCount,
                    coverage.FloorResolvedRecordCount,
                    coverage.UnresolvedRecordCount),
                provenance,
                records));
        }

        if (manifest.Coverage.Any(value => !seenMaps.Contains(value.MapId)))
        {
            throw Refused("coverage.snapshot-missing", "The manifest declares coverage for a map without a content snapshot.");
        }

        var diagnostics = new List<LootSpawnSourceDiagnostic>();
        foreach (var coverage in manifest.Coverage.OrderBy(value => value.MapId, StringComparer.Ordinal))
        {
            if (coverage.PublishedRecordCount < coverage.KnownRecordCount)
            {
                diagnostics.Add(new(
                    "coverage.map-partial",
                    $"Published {coverage.PublishedRecordCount.ToString(CultureInfo.InvariantCulture)} of " +
                    $"{coverage.KnownRecordCount.ToString(CultureInfo.InvariantCulture)} known records.",
                    coverage.MapId));
            }
        }

        foreach (var expectedMapId in context.ExpectedMapIds.Order(StringComparer.Ordinal))
        {
            if (!seenMaps.Contains(expectedMapId))
            {
                diagnostics.Add(new("coverage.map-missing", "No reviewed location bundle is published for this supported map.", expectedMapId));
            }
        }

        return new(
            manifest.Identity,
            Array.AsReadOnly(snapshots.OrderBy(value => value.MapId, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(manifest.Coverage.OrderBy(value => value.MapId, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static LootSpawnRecord ReadRecord(
        JsonElement record,
        LootSpawnMapSourceDefinition map,
        Manifest manifest,
        LootSpawnSourceImportContext context,
        CancellationToken cancellationToken)
    {
        var spawnId = RequiredString(record, "spawnId", 160);
        var label = RequiredString(record, "label", 256);
        var locationObject = RequireObject(Required(record, "location"), "location");
        var precision = ParsePrecision(RequiredString(locationObject, "precision", 32));
        var floorElements = RequiredArray(locationObject, "floorIds", LootSpawnLocation.MaximumFloors);
        var floors = new List<string>(floorElements.GetArrayLength());
        var seenFloors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var floorElement in floorElements.EnumerateArray())
        {
            var floor = RequiredStringValue(floorElement, "floorId", 96);
            if (!map.FloorIds.Contains(floor))
            {
                throw Refused("location.floor-unknown", $"Spawn '{spawnId}' names unknown floor '{floor}'.");
            }

            if (!seenFloors.Add(floor))
            {
                throw Refused("location.floor-duplicate", $"Spawn '{spawnId}' repeats floor '{floor}'.");
            }

            floors.Add(floor);
        }

        var points = ReadPoints(locationObject, precision, map, spawnId, cancellationToken);
        if (precision == LootSpawnPrecision.MapOnly && floors.Count > 0)
        {
            throw Refused("location.map-only-floor", $"Map-only spawn '{spawnId}' cannot claim floor precision.");
        }

        var poolObject = RequireObject(Required(record, "pool"), "pool");
        var poolKind = ParsePoolKind(RequiredString(poolObject, "kind", 32));
        var itemElements = RequiredArray(poolObject, "itemIds", LootSpawnRecord.MaximumCandidates);
        var candidates = new List<LootSpawnCandidate>(itemElements.GetArrayLength());
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemElement in itemElements.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemId = RequiredStringValue(itemElement, "itemId", 256);
            if (!seenItems.Add(itemId))
            {
                throw Refused("pool.item-duplicate", $"Spawn '{spawnId}' repeats item '{itemId}'.");
            }

            if (!context.Items.TryGetValue(itemId, out var item))
            {
                throw Refused("pool.item-unknown", $"Spawn '{spawnId}' names item '{itemId}' absent from json.tarkov.dev metadata.");
            }

            ValidateItemMetadata(itemId, item, context);
            candidates.Add(new(
                item.ItemId,
                item.DisplayName,
                item.Category,
                item.FleaGrossRoubles,
                item.FleaNetRoubles,
                item.BestTraderRoubles,
                item.OccupiedSquares));
        }

        if (candidates.Count == 0)
        {
            throw Refused("pool.empty", $"Spawn '{spawnId}' has no candidate items.");
        }

        var accessNote = OptionalString(record, "accessNote", MaximumStringLength);
        var coverage = manifest.Coverage.Single(value => string.Equals(value.MapId, map.MapId, StringComparison.Ordinal));
        var provenance = Provenance(manifest, context.ImportedUtc, coverage);
        var unknownStatus = new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "source.not-published");
        return new(
            spawnId,
            map.MapId,
            label,
            new LootSpawnLocation(precision, points, floors),
            poolKind,
            candidates,
            new EvidencedValue<double?>("spawn-probability", null, unknownStatus, provenance),
            new EvidencedValue<string?>("respawn-behavior", null, unknownStatus, provenance),
            manifest.DatasetVersion,
            map.TransformVersion,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "loot-spawn-source.current"),
            provenance,
            accessNote);
    }

    private static IReadOnlyList<MapScenePoint>? ReadPoints(
        JsonElement location,
        LootSpawnPrecision precision,
        LootSpawnMapSourceDefinition map,
        string spawnId,
        CancellationToken cancellationToken)
    {
        if (precision == LootSpawnPrecision.MapOnly)
        {
            if (location.TryGetProperty("points", out var absentPoints) &&
                (absentPoints.ValueKind != JsonValueKind.Array || absentPoints.GetArrayLength() != 0))
            {
                throw Refused("location.map-only-geometry", $"Map-only spawn '{spawnId}' cannot carry geometry.");
            }

            return null;
        }

        var elements = RequiredArray(location, "points", LootSpawnLocation.MaximumGeometryPoints);
        var points = new List<MapScenePoint>(elements.GetArrayLength());
        foreach (var element in elements.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pointObject = RequireObject(element, "point");
            var x = RequiredFiniteDouble(pointObject, "x");
            var y = RequiredFiniteDouble(pointObject, "y");
            var point = new MapScenePoint(x, y);
            if (!map.Bounds.Contains(point))
            {
                throw Refused("location.out-of-bounds", $"Spawn '{spawnId}' has geometry outside reviewed map bounds.");
            }

            points.Add(point);
        }

        return points;
    }

    private static Manifest ReadManifest(JsonElement root, LootSpawnSourceImportContext context)
    {
        var manifest = RequireObject(root, "manifest");
        RequireSchema(manifest);
        var datasetVersion = RequiredString(manifest, "datasetVersion", 128);
        var contentSha256 = RequiredString(manifest, "contentSha256", 64);
        if (contentSha256.Length != 64 || contentSha256.Any(value =>
                !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))))
        {
            throw Refused("manifest.hash-invalid", "The manifest content SHA-256 must be 64 hexadecimal characters.");
        }

        var generatedUtc = RequiredUtc(manifest, "generatedUtc");
        var dataThroughUtc = RequiredUtc(manifest, "dataThroughUtc");
        if (dataThroughUtc > generatedUtc)
        {
            throw Refused("manifest.time-order-invalid", "Data-through time cannot be later than generation time.");
        }

        if (generatedUtc > context.ImportedUtc || dataThroughUtc > context.ImportedUtc)
        {
            throw Refused("manifest.time-future", "The source timestamp is too far in the future.");
        }

        if (context.ImportedUtc - dataThroughUtc > context.MaximumSourceAge)
        {
            throw Refused("manifest.stale", "The source is older than the configured maximum source age.");
        }

        var source = RequireObject(Required(manifest, "source"), "source");
        var sourceClass = ParseSourceClass(RequiredString(source, "class", 32));
        var sourceIdentifier = RequiredString(source, "identifier", 256);
        if (sourceClass == EvidenceSourceClass.PublicStructuredData &&
            !IsTarkovDevIdentifier(sourceIdentifier))
        {
            throw Refused(
                "manifest.source-incompatible",
                "Public structured loot-spawn locations must identify json.tarkov.dev as the primary source.");
        }

        var sourceReference = RequiredString(source, "reference", MaximumStringLength);
        var license = RequiredString(source, "license", 256);
        var confidence = ReadConfidence(RequireObject(Required(source, "confidence"), "confidence"));
        if (sourceClass == EvidenceSourceClass.PublicStructuredData &&
            !IsCanonicalTarkovDevEndpoint(sourceIdentifier, sourceReference, "maps"))
        {
            throw Refused(
                "manifest.source-reference-incompatible",
                "Public structured loot-spawn locations must use the canonical json.tarkov.dev maps endpoint.");
        }

        if (!IsCanonicalTarkovDevEndpoint(
                context.ItemCatalogAuthority.SourceIdentifier,
                context.ItemCatalogAuthority.SourceReference,
                "items"))
        {
            throw Refused(
                "item.source-authority-incompatible",
                "The import context must bind candidate metadata to one canonical json.tarkov.dev items endpoint.");
        }

        if (sourceClass == EvidenceSourceClass.PublicStructuredData &&
            !string.Equals(
                TarkovDevMode(sourceIdentifier, "maps"),
                TarkovDevMode(context.ItemCatalogAuthority.SourceIdentifier, "items"),
                StringComparison.Ordinal))
        {
            throw Refused(
                "manifest.source-mode-mismatch",
                "The maps bundle and candidate item catalog must use the same json.tarkov.dev game mode.");
        }

        if (!context.ReviewedSourceAuthorities.Any(authority =>
                authority.SourceClass == sourceClass &&
                string.Equals(authority.SourceIdentifier, sourceIdentifier, StringComparison.Ordinal) &&
                string.Equals(authority.SourceReference, sourceReference, StringComparison.Ordinal) &&
                string.Equals(authority.License, license, StringComparison.Ordinal) &&
                authority.Confidence == confidence))
        {
            throw Refused(
                "manifest.source-unreviewed",
                "The loot-spawn source authority, terms, or confidence have not been reviewed for this import context.");
        }

        var producerObject = RequireObject(Required(manifest, "producer"), "producer");
        var producer = new ProducerIdentity(
            RequiredString(producerObject, "name", 128),
            RequiredString(producerObject, "version", 128));

        var coverageElements = RequiredArray(manifest, "coverage", MaximumMaps);
        var coverage = new List<LootSpawnMapSourceCoverage>(coverageElements.GetArrayLength());
        var seenMaps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in coverageElements.EnumerateArray())
        {
            var entry = RequireObject(element, "coverage");
            var mapId = RequiredString(entry, "mapId", 128);
            if (!seenMaps.Add(mapId))
            {
                throw Refused("coverage.map-duplicate", $"Map '{mapId}' has duplicate coverage declarations.");
            }

            var known = RequiredNonNegativeInt(entry, "knownRecords");
            var published = RequiredNonNegativeInt(entry, "publishedRecords");
            var positioned = RequiredNonNegativeInt(entry, "positionedRecords");
            var floorResolved = RequiredNonNegativeInt(entry, "floorResolvedRecords");
            var unresolved = RequiredNonNegativeInt(entry, "unresolvedRecords");
            if (published > known || positioned > published || floorResolved > positioned ||
                unresolved > published || positioned + unresolved != published)
            {
                throw Refused("coverage.counts-invalid", $"Coverage counts for map '{mapId}' do not reconcile.");
            }

            coverage.Add(new(mapId, known, published, positioned, floorResolved, unresolved));
        }

        var identity = new LootSpawnSourceIdentity(
            SupportedSchemaVersion,
            datasetVersion,
            contentSha256,
            generatedUtc,
            dataThroughUtc,
            context.ImportedUtc,
            sourceClass,
            sourceIdentifier,
            sourceReference,
            license,
            confidence,
            producer,
            [new LootSpawnSourceArtifactIdentity("loot-spawn-content", sourceIdentifier, contentSha256)]);
        return new(identity, Array.AsReadOnly(coverage.OrderBy(value => value.MapId, StringComparer.Ordinal).ToArray()));
    }

    private static EvidenceProvenance Provenance(
        Manifest manifest,
        DateTimeOffset importedUtc,
        LootSpawnMapSourceCoverage coverage) => new(
        manifest.Identity.SourceClass,
        manifest.Identity.SourceIdentifier,
        importedUtc,
        manifest.Identity.Confidence,
        manifest.Identity.Producer,
        manifest.Identity.DataThroughUtc,
        manifest.Identity.GeneratedUtc,
        new EvidenceCoverage(
            coverage.PublishedRecordCount,
            coverage.KnownRecordCount == 0 ? null : (double)coverage.PublishedRecordCount / coverage.KnownRecordCount,
            $"{coverage.PublishedRecordCount.ToString(CultureInfo.InvariantCulture)} of " +
            $"{coverage.KnownRecordCount.ToString(CultureInfo.InvariantCulture)} known records for {coverage.MapId}"),
        manifest.Identity.SourceReference);

    private static void ValidateMeasuredCoverage(
        LootSpawnMapSourceCoverage coverage,
        IReadOnlyCollection<LootSpawnRecord> records)
    {
        var positioned = records.Count(record => record.Location.Geometry is not null);
        var floorResolved = records.Count(record => record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        if (coverage.PublishedRecordCount != records.Count || coverage.PositionedRecordCount != positioned ||
            coverage.FloorResolvedRecordCount != floorResolved ||
            coverage.UnresolvedRecordCount != records.Count - positioned)
        {
            throw Refused("coverage.measured-mismatch", $"Coverage for map '{coverage.MapId}' does not match parsed records.");
        }
    }

    private static void ValidateItemMetadata(
        string requestedItemId,
        LootSpawnItemCatalogEntry item,
        LootSpawnSourceImportContext context)
    {
        if (!string.Equals(requestedItemId, item.ItemId, StringComparison.Ordinal))
        {
            throw Refused("item.identity-mismatch", $"Item '{requestedItemId}' resolved to a different canonical item identity.");
        }

        ValidateItemField(item.ItemId, item.FleaGrossRoubles, "flea-gross", context);
        ValidateItemField(item.ItemId, item.FleaNetRoubles, "flea-net", context);
        ValidateItemField(item.ItemId, item.BestTraderRoubles, "best-trader", context);
        ValidateItemField(item.ItemId, item.OccupiedSquares, "occupied-squares", context);
    }

    private static void ValidateItemField<T>(
        string itemId,
        EvidencedValue<T?> field,
        string expectedFieldId,
        LootSpawnSourceImportContext context)
        where T : struct
    {
        if (!string.Equals(field.FieldId, expectedFieldId, StringComparison.Ordinal) ||
            field.Provenance.SourceClass != EvidenceSourceClass.PublicStructuredData ||
            !string.Equals(
                field.Provenance.SourceIdentifier,
                context.ItemCatalogAuthority.SourceIdentifier,
                StringComparison.Ordinal) ||
            !string.Equals(
                field.Provenance.Reference,
                context.ItemCatalogAuthority.SourceReference,
                StringComparison.Ordinal))
        {
            throw Refused("item.source-incompatible", $"Item '{itemId}' field '{expectedFieldId}' does not carry canonical json.tarkov.dev provenance.");
        }

        ValidateProvenanceChronology(field.Provenance, context.ImportedUtc, itemId, expectedFieldId);
        if (field.Candidates.Count > LootSpawnCandidate.MaximumEvidenceAlternatives ||
            field.Corrections.Count > LootSpawnCandidate.MaximumEvidenceAlternatives ||
            !IsBoundedOptional(field.Status.Code, 128) ||
            !IsBoundedOptional(field.Status.Detail, MaximumStringLength) ||
            field.Candidates.Any(candidate =>
                !IsBoundedRequired(candidate.CandidateId, 256) ||
                !IsBoundedRequired(candidate.DisplayName, 256)) ||
            field.Corrections.Any(correction =>
                correction.CorrectedUtc > context.ImportedUtc ||
                !IsBoundedRequired(correction.OriginIdentifier, 256) ||
                !IsBoundedOptional(correction.Reason, 512)))
        {
            throw Refused("item.evidence-invalid", $"Item '{itemId}' field '{expectedFieldId}' exceeds evidence bounds or contains a future correction.");
        }

        foreach (var candidate in field.Candidates)
        {
            ValidateProvenanceChronology(candidate.Provenance, context.ImportedUtc, itemId, expectedFieldId);
        }
    }

    private static void ValidateProvenanceChronology(
        EvidenceProvenance provenance,
        DateTimeOffset importedUtc,
        string itemId,
        string fieldId)
    {
        if (provenance.SourceClass == EvidenceSourceClass.Unknown ||
            provenance.ObservedUtc > importedUtc ||
            provenance.DataThroughUtc > importedUtc ||
            provenance.GeneratedUtc > importedUtc ||
            !IsBoundedRequired(provenance.SourceIdentifier, 256) ||
            !IsBoundedOptional(provenance.Reference, MaximumStringLength) ||
            !IsBoundedRequired(provenance.Producer.Name, 128) ||
            !IsBoundedRequired(provenance.Producer.Version, 128) ||
            !IsBoundedOptional(provenance.Producer.ModelVersion, 128) ||
            !IsBoundedOptional(provenance.Confidence.CalibrationReference, 512) ||
            !IsBoundedOptional(provenance.Coverage?.Description, MaximumStringLength))
        {
            throw Refused("item.evidence-invalid", $"Item '{itemId}' field '{fieldId}' carries unknown or future evidence.");
        }

        foreach (var input in provenance.Inputs)
        {
            ValidateProvenanceChronology(input, importedUtc, itemId, fieldId);
        }
    }

    private static bool IsTarkovDevIdentifier(string value) =>
        string.Equals(value, "json.tarkov.dev", StringComparison.Ordinal) ||
        value.StartsWith("json.tarkov.dev/", StringComparison.Ordinal);

    private static bool IsCanonicalTarkovDevEndpoint(
        string sourceIdentifier,
        string? sourceReference,
        string resource)
    {
        foreach (var mode in new[] { "regular", "pve", "pvp-season" })
        {
            if (string.Equals(sourceIdentifier, $"json.tarkov.dev/{mode}/{resource}", StringComparison.Ordinal) &&
                string.Equals(sourceReference, $"https://json.tarkov.dev/{mode}/{resource}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TarkovDevMode(string sourceIdentifier, string resource)
    {
        foreach (var mode in new[] { "regular", "pve", "pvp-season" })
        {
            if (string.Equals(sourceIdentifier, $"json.tarkov.dev/{mode}/{resource}", StringComparison.Ordinal))
            {
                return mode;
            }
        }

        return null;
    }

    private static bool IsBoundedRequired(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static bool IsBoundedOptional(string? value, int maximum) =>
        value is null || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximum);

    private static LootSpawnPrecision ParsePrecision(string value) => value switch
    {
        "exactPoint" => LootSpawnPrecision.ExactPoint,
        "boundedArea" => LootSpawnPrecision.BoundedArea,
        "roomOrRegion" => LootSpawnPrecision.RoomOrRegion,
        "mapOnly" => LootSpawnPrecision.MapOnly,
        _ => throw Refused("location.precision-unknown", $"Unknown location precision '{value}'."),
    };

    private static LootSpawnPoolKind ParsePoolKind(string value) => value switch
    {
        "singleKnownItem" => LootSpawnPoolKind.SingleKnownItem,
        "unweightedCandidates" => LootSpawnPoolKind.UnweightedCandidates,
        _ => throw Refused("pool.kind-unknown", $"Unknown candidate-pool kind '{value}'."),
    };

    private static EvidenceSourceClass ParseSourceClass(string value) => value switch
    {
        "curatedData" => EvidenceSourceClass.CuratedData,
        "publicStructuredData" => EvidenceSourceClass.PublicStructuredData,
        _ => throw Refused("manifest.source-class-incompatible", $"Unsupported source class '{value}'."),
    };

    private static EvidenceConfidence ReadConfidence(JsonElement element)
    {
        var kind = RequiredString(element, "kind", 32);
        var score = element.TryGetProperty("score", out var scoreElement) && scoreElement.ValueKind != JsonValueKind.Null
            ? scoreElement.GetDouble()
            : (double?)null;
        if (score is { } present && !double.IsFinite(present))
        {
            throw Refused("manifest.confidence-invalid", "Confidence must be finite.");
        }

        var calibration = OptionalString(element, "calibrationReference", 512);
        return kind switch
        {
            "unscored" => new(EvidenceConfidenceKind.Unscored),
            "deterministic" => new(EvidenceConfidenceKind.Deterministic, score),
            "providerScore" => new(EvidenceConfidenceKind.ProviderScore, score),
            "calibratedEstimate" => new(EvidenceConfidenceKind.CalibratedEstimate, score, calibration),
            _ => throw Refused("manifest.confidence-kind-unknown", $"Unknown confidence kind '{kind}'."),
        };
    }

    private static void RequireSchema(JsonElement element)
    {
        var schema = RequiredNonNegativeInt(element, "schemaVersion");
        if (schema != SupportedSchemaVersion)
        {
            throw Refused("schema.incompatible", $"Schema {schema.ToString(CultureInfo.InvariantCulture)} is not supported.");
        }
    }

    private static void BoundDocument(ReadOnlyMemory<byte> bytes, int maximum, string name)
    {
        if (bytes.Length is < 1 || bytes.Length > maximum)
        {
            throw Refused("source.size-invalid", $"The {name} must contain between 1 and {maximum.ToString(CultureInfo.InvariantCulture)} bytes.");
        }
    }

    // JsonDocument deliberately permits duplicate object members. Reject them recursively before
    // reading any claim: otherwise two conforming tools can hash the same bytes but disagree about
    // which schema, source, timestamp, or coordinate the document asserts.
    private static void ValidateJsonShape(JsonElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!names.Add(property.Name))
                {
                    throw Refused("source.duplicate-property", $"JSON object member '{property.Name}' is repeated.");
                }

                ValidateJsonShape(property.Value, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                ValidateJsonShape(value, cancellationToken);
            }
        }
    }

    private static JsonElement Required(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value
            : throw Refused("source.required-field-missing", $"Required field '{propertyName}' is missing.");

    private static JsonElement RequireObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw Refused("source.shape-invalid", $"'{name}' must be an object.");

    private static JsonElement RequiredArray(JsonElement element, string propertyName, int maximum)
    {
        var array = Required(element, propertyName);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Refused("source.shape-invalid", $"'{propertyName}' must be an array.");
        }

        if (array.GetArrayLength() > maximum)
        {
            throw Refused("source.collection-oversized", $"'{propertyName}' exceeds its {maximum.ToString(CultureInfo.InvariantCulture)} entry limit.");
        }

        return array;
    }

    private static string RequiredString(JsonElement element, string propertyName, int maximum) =>
        RequiredStringValue(Required(element, propertyName), propertyName, maximum);

    private static string RequiredStringValue(JsonElement element, string name, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Refused("source.shape-invalid", $"'{name}' must be a string.");
        }

        return RequiredText(element.GetString(), name, maximum);
    }

    private static string RequiredText(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Refused("source.required-field-invalid", $"'{name}' must be non-empty.");
        }

        var trimmed = value.Trim();
        if (!string.Equals(value, trimmed, StringComparison.Ordinal) ||
            !value.IsNormalized(NormalizationForm.FormC))
        {
            throw Refused("source.string-noncanonical", $"'{name}' must not contain surrounding whitespace or non-canonical Unicode.");
        }

        if (value.Length > maximum)
        {
            throw Refused("source.string-oversized", $"'{name}' exceeds its {maximum.ToString(CultureInfo.InvariantCulture)} character limit.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement element, string propertyName, int maximum)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequiredStringValue(value, propertyName, maximum);
    }

    private static DateTimeOffset RequiredUtc(JsonElement element, string propertyName)
    {
        var text = RequiredString(element, propertyName, 64);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value) || value.Offset != TimeSpan.Zero || value == default)
        {
            throw Refused("manifest.time-invalid", $"'{propertyName}' must be a canonical UTC timestamp.");
        }

        return value;
    }

    private static int RequiredNonNegativeInt(JsonElement element, string propertyName)
    {
        var value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
        {
            throw Refused("source.number-invalid", $"'{propertyName}' must be a non-negative 32-bit integer.");
        }

        return number;
    }

    private static double RequiredFiniteDouble(JsonElement element, string propertyName)
    {
        var value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw Refused("source.number-invalid", $"'{propertyName}' must be a finite number.");
        }

        return number;
    }

    private static LootSpawnSourceImportException Refused(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private sealed record Manifest(
        LootSpawnSourceIdentity Identity,
        ReadOnlyCollection<LootSpawnMapSourceCoverage> Coverage)
    {
        public string DatasetVersion => Identity.DatasetVersion;

        public string ContentSha256 => Identity.ContentSha256;

        public DateTimeOffset GeneratedUtc => Identity.GeneratedUtc;
    }
}
