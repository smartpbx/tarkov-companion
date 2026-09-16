using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.LootSpawns;

public sealed record LootSpawnItemCatalogEntry
{
    public LootSpawnItemCatalogEntry(
        string itemId,
        string displayName,
        string category,
        EvidencedValue<long?> fleaGrossRoubles,
        EvidencedValue<long?> fleaNetRoubles,
        EvidencedValue<long?> bestTraderRoubles,
        EvidencedValue<int?> occupiedSquares)
    {
        ItemId = Required(itemId, nameof(itemId), 256);
        DisplayName = Required(displayName, nameof(displayName), 256);
        Category = Required(category, nameof(category), 128);
        FleaGrossRoubles = fleaGrossRoubles ?? throw new ArgumentNullException(nameof(fleaGrossRoubles));
        FleaNetRoubles = fleaNetRoubles ?? throw new ArgumentNullException(nameof(fleaNetRoubles));
        BestTraderRoubles = bestTraderRoubles ?? throw new ArgumentNullException(nameof(bestTraderRoubles));
        OccupiedSquares = occupiedSquares ?? throw new ArgumentNullException(nameof(occupiedSquares));
    }

    public string ItemId { get; }

    public string DisplayName { get; }

    public string Category { get; }

    public EvidencedValue<long?> FleaGrossRoubles { get; }

    public EvidencedValue<long?> FleaNetRoubles { get; }

    public EvidencedValue<long?> BestTraderRoubles { get; }

    public EvidencedValue<int?> OccupiedSquares { get; }

    internal static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !value.IsNormalized(NormalizationForm.FormC) ||
            value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed record LootSpawnMapSourceDefinition
{
    public LootSpawnMapSourceDefinition(
        string mapId,
        string transformVersion,
        MapSceneBounds bounds,
        IReadOnlySet<string> floorIds)
    {
        MapId = LootSpawnItemCatalogEntry.Required(mapId, nameof(mapId), 128);
        TransformVersion = LootSpawnItemCatalogEntry.Required(transformVersion, nameof(transformVersion), 128);
        if (!double.IsFinite(bounds.MinimumX) || !double.IsFinite(bounds.MinimumY) ||
            !double.IsFinite(bounds.MaximumX) || !double.IsFinite(bounds.MaximumY) ||
            bounds.MaximumX <= bounds.MinimumX || bounds.MaximumY <= bounds.MinimumY)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        ArgumentNullException.ThrowIfNull(floorIds);
        var copiedFloors = floorIds.Take(LootSpawnLocation.MaximumFloors + 1).ToArray();
        if (copiedFloors.Length > LootSpawnLocation.MaximumFloors)
        {
            throw new ArgumentException("The reviewed map has too many floor identifiers.", nameof(floorIds));
        }

        foreach (var floorId in copiedFloors)
        {
            LootSpawnItemCatalogEntry.Required(floorId, nameof(floorIds), 96);
        }

        if (copiedFloors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copiedFloors.Length)
        {
            throw new ArgumentException("Reviewed floor identifiers must be unique without case aliases.", nameof(floorIds));
        }

        Bounds = bounds;
        FloorIds = copiedFloors.ToFrozenSet(StringComparer.Ordinal);
    }

    public string MapId { get; }

    public string TransformVersion { get; }

    public MapSceneBounds Bounds { get; }

    public IReadOnlySet<string> FloorIds { get; }
}

/// <summary>A source identity whose authority, terms, and confidence were reviewed out of band.</summary>
public sealed record LootSpawnSourceAuthority
{
    public LootSpawnSourceAuthority(
        EvidenceSourceClass sourceClass,
        string sourceIdentifier,
        string sourceReference,
        string license,
        EvidenceConfidence confidence)
    {
        if (sourceClass is not EvidenceSourceClass.PublicStructuredData and not EvidenceSourceClass.CuratedData)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceClass));
        }

        SourceClass = sourceClass;
        SourceIdentifier = LootSpawnItemCatalogEntry.Required(sourceIdentifier, nameof(sourceIdentifier), 256);
        SourceReference = LootSpawnItemCatalogEntry.Required(sourceReference, nameof(sourceReference), 1024);
        License = LootSpawnItemCatalogEntry.Required(license, nameof(license), 256);
        Confidence = confidence ?? throw new ArgumentNullException(nameof(confidence));
    }

    public EvidenceSourceClass SourceClass { get; }

    public string SourceIdentifier { get; }

    public string SourceReference { get; }

    public string License { get; }

    public EvidenceConfidence Confidence { get; }
}

/// <summary>The exact active-mode item catalog that may resolve bundle candidate IDs.</summary>
public sealed record LootSpawnItemCatalogAuthority
{
    public LootSpawnItemCatalogAuthority(string sourceIdentifier, string sourceReference)
    {
        SourceIdentifier = LootSpawnItemCatalogEntry.Required(sourceIdentifier, nameof(sourceIdentifier), 256);
        SourceReference = LootSpawnItemCatalogEntry.Required(sourceReference, nameof(sourceReference), 1024);
    }

    public string SourceIdentifier { get; }

    public string SourceReference { get; }
}

public sealed record LootSpawnSourceDocuments
{
    public LootSpawnSourceDocuments(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> content)
    {
        Manifest = manifest.ToArray();
        Content = content.ToArray();
    }

    public ReadOnlyMemory<byte> Manifest { get; }

    public ReadOnlyMemory<byte> Content { get; }
}

public sealed record LootSpawnSourceImportContext
{
    public const int MaximumCatalogItems = 100_000;

    public const int MaximumMaps = 64;

    public const int MaximumSourceAuthorities = 32;

    public LootSpawnSourceImportContext(
        DateTimeOffset importedUtc,
        TimeSpan maximumSourceAge,
        IReadOnlyDictionary<string, LootSpawnItemCatalogEntry> items,
        IReadOnlyDictionary<string, LootSpawnMapSourceDefinition> maps,
        IReadOnlySet<string> expectedMapIds,
        LootSpawnItemCatalogAuthority itemCatalogAuthority,
        IReadOnlyList<LootSpawnSourceAuthority> reviewedSourceAuthorities)
    {
        if (importedUtc == default || importedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC import time is required.", nameof(importedUtc));
        }

        if (maximumSourceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourceAge));
        }

        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(expectedMapIds);
        ArgumentNullException.ThrowIfNull(itemCatalogAuthority);
        ArgumentNullException.ThrowIfNull(reviewedSourceAuthorities);

        var copiedItems = items.Take(MaximumCatalogItems + 1).ToArray();
        if (copiedItems.Length > MaximumCatalogItems)
        {
            throw new ArgumentException("The item catalog exceeds its import-context limit.", nameof(items));
        }

        var itemDictionary = new Dictionary<string, LootSpawnItemCatalogEntry>(copiedItems.Length, StringComparer.Ordinal);
        foreach (var (key, value) in copiedItems)
        {
            ArgumentNullException.ThrowIfNull(value);
            LootSpawnItemCatalogEntry.Required(key, nameof(items), 256);
            if (!string.Equals(key, value.ItemId, StringComparison.Ordinal) || !itemDictionary.TryAdd(key, value))
            {
                throw new ArgumentException("Item catalog keys must uniquely equal their canonical item IDs.", nameof(items));
            }
        }

        var copiedMaps = maps.Take(MaximumMaps + 1).ToArray();
        if (copiedMaps.Length > MaximumMaps)
        {
            throw new ArgumentException("The map catalog exceeds its import-context limit.", nameof(maps));
        }

        var mapDictionary = new Dictionary<string, LootSpawnMapSourceDefinition>(copiedMaps.Length, StringComparer.Ordinal);
        foreach (var (key, value) in copiedMaps)
        {
            ArgumentNullException.ThrowIfNull(value);
            LootSpawnItemCatalogEntry.Required(key, nameof(maps), 128);
            if (!string.Equals(key, value.MapId, StringComparison.Ordinal) || !mapDictionary.TryAdd(key, value))
            {
                throw new ArgumentException("Map catalog keys must uniquely equal their canonical map IDs.", nameof(maps));
            }
        }

        var expected = expectedMapIds.Take(MaximumMaps + 1).ToArray();
        if (expected.Length > MaximumMaps)
        {
            throw new ArgumentException("The supported-map set exceeds its import-context limit.", nameof(expectedMapIds));
        }

        var expectedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapId in expected)
        {
            LootSpawnItemCatalogEntry.Required(mapId, nameof(expectedMapIds), 128);
            if (!mapDictionary.ContainsKey(mapId) || !expectedSet.Add(mapId))
            {
                throw new ArgumentException("Every expected map must uniquely exist in the reviewed map catalog.", nameof(expectedMapIds));
            }
        }

        var authorities = reviewedSourceAuthorities.Take(MaximumSourceAuthorities + 1).ToArray();
        if (authorities.Length is < 1 or > MaximumSourceAuthorities || authorities.Any(value => value is null))
        {
            throw new ArgumentException("A bounded, non-empty reviewed source-authority set is required.", nameof(reviewedSourceAuthorities));
        }

        if (authorities.Distinct().Count() != authorities.Length)
        {
            throw new ArgumentException("Reviewed source authorities must be unique.", nameof(reviewedSourceAuthorities));
        }

        ImportedUtc = importedUtc;
        MaximumSourceAge = maximumSourceAge;
        Items = new ReadOnlyDictionary<string, LootSpawnItemCatalogEntry>(itemDictionary);
        Maps = new ReadOnlyDictionary<string, LootSpawnMapSourceDefinition>(mapDictionary);
        ExpectedMapIds = expectedSet.ToFrozenSet(StringComparer.Ordinal);
        ItemCatalogAuthority = itemCatalogAuthority;
        ReviewedSourceAuthorities = Array.AsReadOnly(authorities);
    }

    public DateTimeOffset ImportedUtc { get; }

    public TimeSpan MaximumSourceAge { get; }

    public IReadOnlyDictionary<string, LootSpawnItemCatalogEntry> Items { get; }

    public IReadOnlyDictionary<string, LootSpawnMapSourceDefinition> Maps { get; }

    public IReadOnlySet<string> ExpectedMapIds { get; }

    public LootSpawnItemCatalogAuthority ItemCatalogAuthority { get; }

    public IReadOnlyList<LootSpawnSourceAuthority> ReviewedSourceAuthorities { get; }
}

public sealed record LootSpawnMapSourceCoverage
{
    public LootSpawnMapSourceCoverage(
        string mapId,
        int knownRecordCount,
        int publishedRecordCount,
        int positionedRecordCount,
        int floorResolvedRecordCount,
        int unresolvedRecordCount)
    {
        MapId = LootSpawnItemCatalogEntry.Required(mapId, nameof(mapId), 128);
        if (knownRecordCount < 0 || publishedRecordCount < 0 || positionedRecordCount < 0 ||
            floorResolvedRecordCount < 0 || unresolvedRecordCount < 0 ||
            publishedRecordCount > knownRecordCount || positionedRecordCount > publishedRecordCount ||
            floorResolvedRecordCount > positionedRecordCount ||
            positionedRecordCount + unresolvedRecordCount != publishedRecordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(knownRecordCount), "Coverage counts do not reconcile.");
        }

        KnownRecordCount = knownRecordCount;
        PublishedRecordCount = publishedRecordCount;
        PositionedRecordCount = positionedRecordCount;
        FloorResolvedRecordCount = floorResolvedRecordCount;
        UnresolvedRecordCount = unresolvedRecordCount;
    }

    public string MapId { get; }

    public int KnownRecordCount { get; }

    public int PublishedRecordCount { get; }

    public int PositionedRecordCount { get; }

    public int FloorResolvedRecordCount { get; }

    public int UnresolvedRecordCount { get; }
}

/// <summary>One exact input document whose bytes contributed to a published bundle.</summary>
public sealed record LootSpawnSourceArtifactIdentity
{
    public LootSpawnSourceArtifactIdentity(string role, string sourceIdentifier, string contentSha256)
    {
        Role = LootSpawnItemCatalogEntry.Required(role, nameof(role), 64);
        SourceIdentifier = LootSpawnItemCatalogEntry.Required(sourceIdentifier, nameof(sourceIdentifier), 1024);
        ContentSha256 = LootSpawnItemCatalogEntry.Required(contentSha256, nameof(contentSha256), 64);
        if (ContentSha256.Length != 64 || ContentSha256.Any(value =>
                !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))))
        {
            throw new ArgumentException("A lowercase SHA-256 artifact identity is required.", nameof(contentSha256));
        }
    }

    public string Role { get; }

    public string SourceIdentifier { get; }

    public string ContentSha256 { get; }
}

public sealed record LootSpawnSourceIdentity
{
    public const int MaximumArtifacts = 8;

    public LootSpawnSourceIdentity(
        int schemaVersion,
        string datasetVersion,
        string contentSha256,
        DateTimeOffset generatedUtc,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset importedUtc,
        EvidenceSourceClass sourceClass,
        string sourceIdentifier,
        string sourceReference,
        string license,
        EvidenceConfidence confidence,
        ProducerIdentity producer,
        IReadOnlyList<LootSpawnSourceArtifactIdentity>? artifacts = null)
    {
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        DatasetVersion = LootSpawnItemCatalogEntry.Required(datasetVersion, nameof(datasetVersion), 128);
        ContentSha256 = LootSpawnItemCatalogEntry.Required(contentSha256, nameof(contentSha256), 64);
        if (ContentSha256.Length != 64 || ContentSha256.Any(value =>
                !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))))
        {
            throw new ArgumentException("A SHA-256 content identity is required.", nameof(contentSha256));
        }

        if (generatedUtc == default || dataThroughUtc == default || importedUtc == default ||
            generatedUtc.Offset != TimeSpan.Zero || dataThroughUtc.Offset != TimeSpan.Zero ||
            importedUtc.Offset != TimeSpan.Zero || dataThroughUtc > generatedUtc || generatedUtc > importedUtc)
        {
            throw new ArgumentException("Source identity timestamps must be ordered, non-default UTC values.", nameof(generatedUtc));
        }

        if (sourceClass is not EvidenceSourceClass.PublicStructuredData and not EvidenceSourceClass.CuratedData)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceClass));
        }

        SchemaVersion = schemaVersion;
        GeneratedUtc = generatedUtc;
        DataThroughUtc = dataThroughUtc;
        ImportedUtc = importedUtc;
        SourceClass = sourceClass;
        SourceIdentifier = LootSpawnItemCatalogEntry.Required(sourceIdentifier, nameof(sourceIdentifier), 256);
        SourceReference = LootSpawnItemCatalogEntry.Required(sourceReference, nameof(sourceReference), 1024);
        License = LootSpawnItemCatalogEntry.Required(license, nameof(license), 256);
        Confidence = confidence ?? throw new ArgumentNullException(nameof(confidence));
        Producer = producer ?? throw new ArgumentNullException(nameof(producer));
        var copiedArtifacts = (artifacts ?? [])
            .Take(MaximumArtifacts + 1)
            .ToArray();
        if (copiedArtifacts.Length > MaximumArtifacts || copiedArtifacts.Any(value => value is null) ||
            copiedArtifacts.Select(value => value.Role).Distinct(StringComparer.Ordinal).Count() != copiedArtifacts.Length)
        {
            throw new ArgumentException(
                "Source artifacts must be bounded and have unique ordinal roles.",
                nameof(artifacts));
        }

        Array.Sort(copiedArtifacts, static (left, right) => StringComparer.Ordinal.Compare(left.Role, right.Role));
        Artifacts = Array.AsReadOnly(copiedArtifacts);
    }

    public int SchemaVersion { get; }

    public string DatasetVersion { get; }

    public string ContentSha256 { get; }

    public DateTimeOffset GeneratedUtc { get; }

    public DateTimeOffset DataThroughUtc { get; }

    public DateTimeOffset ImportedUtc { get; }

    public EvidenceSourceClass SourceClass { get; }

    public string SourceIdentifier { get; }

    public string SourceReference { get; }

    public string License { get; }

    public EvidenceConfidence Confidence { get; }

    public ProducerIdentity Producer { get; }

    /// <summary>Exact hashes of the source documents framed by <see cref="ContentSha256"/>.</summary>
    public IReadOnlyList<LootSpawnSourceArtifactIdentity> Artifacts { get; }
}

public sealed record LootSpawnSourceDiagnostic
{
    public LootSpawnSourceDiagnostic(string code, string detail, string? mapId = null)
    {
        Code = LootSpawnItemCatalogEntry.Required(code, nameof(code), 128);
        Detail = LootSpawnItemCatalogEntry.Required(detail, nameof(detail), 1024);
        MapId = mapId is null ? null : LootSpawnItemCatalogEntry.Required(mapId, nameof(mapId), 128);
    }

    public string Code { get; }

    public string Detail { get; }

    public string? MapId { get; }
}

public sealed record LootSpawnSourceBundle
{
    public const int MaximumDiagnostics = LootSpawnSourceImportContext.MaximumMaps * 2;

    public LootSpawnSourceBundle(
        LootSpawnSourceIdentity identity,
        IReadOnlyList<LootSpawnSnapshot> snapshots,
        IReadOnlyList<LootSpawnMapSourceCoverage> coverage,
        IReadOnlyList<LootSpawnSourceDiagnostic> diagnostics)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Snapshots = BoundedCopy(snapshots, LootSpawnSourceImportContext.MaximumMaps, nameof(snapshots));
        Coverage = BoundedCopy(coverage, LootSpawnSourceImportContext.MaximumMaps, nameof(coverage));
        Diagnostics = BoundedCopy(diagnostics, MaximumDiagnostics, nameof(diagnostics));

        if (Snapshots.Select(value => value.SnapshotId).Distinct(StringComparer.Ordinal).Count() != Snapshots.Count ||
            Snapshots.Select(value => value.MapId).Distinct(StringComparer.Ordinal).Count() != Snapshots.Count ||
            Coverage.Select(value => value.MapId).Distinct(StringComparer.Ordinal).Count() != Coverage.Count ||
            Snapshots.Any(value => !string.Equals(value.DatasetVersion, identity.DatasetVersion, StringComparison.Ordinal)) ||
            !Snapshots.Select(value => value.MapId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(Coverage.Select(value => value.MapId)))
        {
            throw new ArgumentException("Bundle snapshots, coverage, and source identity do not reconcile.");
        }

        foreach (var snapshot in Snapshots)
        {
            var measured = Coverage.Single(value => string.Equals(value.MapId, snapshot.MapId, StringComparison.Ordinal));
            if (snapshot.GeneratedUtc != identity.ImportedUtc ||
                snapshot.Coverage.Published != measured.PublishedRecordCount ||
                snapshot.Coverage.Positioned != measured.PositionedRecordCount ||
                snapshot.Coverage.FloorResolved != measured.FloorResolvedRecordCount ||
                snapshot.Coverage.Unresolved != measured.UnresolvedRecordCount ||
                snapshot.Provenance.SourceClass != identity.SourceClass ||
                !string.Equals(snapshot.Provenance.SourceIdentifier, identity.SourceIdentifier, StringComparison.Ordinal) ||
                snapshot.Provenance.ObservedUtc != identity.ImportedUtc ||
                snapshot.Provenance.DataThroughUtc != identity.DataThroughUtc ||
                snapshot.Provenance.GeneratedUtc != identity.GeneratedUtc ||
                snapshot.Provenance.Confidence != identity.Confidence ||
                snapshot.Provenance.Producer != identity.Producer ||
                !string.Equals(snapshot.Provenance.Reference, identity.SourceReference, StringComparison.Ordinal))
            {
                throw new ArgumentException("A bundle snapshot does not match its measured coverage or source identity.", nameof(snapshots));
            }
        }
    }

    public LootSpawnSourceIdentity Identity { get; }

    public IReadOnlyList<LootSpawnSnapshot> Snapshots { get; }

    public IReadOnlyList<LootSpawnMapSourceCoverage> Coverage { get; }

    public IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics { get; }

    private static IReadOnlyList<T> BoundedCopy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = values.Take(maximum + 1).ToArray();
        if (copied.Length > maximum || copied.Any(value => value is null))
        {
            throw new ArgumentException("A source-bundle collection is invalid or oversized.", parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}

public enum LootSpawnSourceImportDisposition
{
    Published = 1,
    PublishedPartial,
    QuarantinedRetainedLastKnownGood,
}

public sealed record LootSpawnSourceImportResult(
    LootSpawnSourceImportDisposition Disposition,
    LootSpawnSourceBundle? Published,
    LootSpawnSourceBundle? LastKnownGood,
    IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics);

public sealed class LootSpawnSourceImportException : Exception
{
    public LootSpawnSourceImportException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public interface ILootSpawnSourceBundleReader
{
    ValueTask<LootSpawnSourceBundle> ReadAsync(
        LootSpawnSourceDocuments documents,
        LootSpawnSourceImportContext context,
        CancellationToken cancellationToken);
}

public interface ILootSpawnSourcePublicationStore
{
    ValueTask<LootSpawnSourceBundle?> ReadLastKnownGoodAsync(CancellationToken cancellationToken);

    ValueTask PublishAsync(LootSpawnSourceBundle bundle, CancellationToken cancellationToken);

    ValueTask QuarantineAsync(
        LootSpawnSourceDiagnostic diagnostic,
        DateTimeOffset detectedUtc,
        CancellationToken cancellationToken);
}

/// <summary>Explicit human authorization for a legitimate wipe or reviewed source shrink.</summary>
public sealed record LootSpawnPublicationReplacementAuthorization
{
    public LootSpawnPublicationReplacementAuthorization(
        string expectedCurrentContentSha256,
        string authorizedBy,
        string reason,
        DateTimeOffset authorizedUtc)
    {
        ExpectedCurrentContentSha256 = RequiredHash(
            expectedCurrentContentSha256,
            nameof(expectedCurrentContentSha256));
        AuthorizedBy = LootSpawnItemCatalogEntry.Required(authorizedBy, nameof(authorizedBy), 128);
        Reason = LootSpawnItemCatalogEntry.Required(reason, nameof(reason), 1024);
        if (authorizedUtc == default || authorizedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC authorization time is required.", nameof(authorizedUtc));
        }

        AuthorizedUtc = authorizedUtc;
    }

    public string ExpectedCurrentContentSha256 { get; }

    public string AuthorizedBy { get; }

    public string Reason { get; }

    public DateTimeOffset AuthorizedUtc { get; }

    private static string RequiredHash(string value, string parameterName)
    {
        var required = LootSpawnItemCatalogEntry.Required(value, parameterName, 64);
        return required.Length == 64 && required.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? required
            : throw new ArgumentException("A lowercase SHA-256 content identity is required.", parameterName);
    }
}

/// <summary>
/// Durable evidence that a specific replacement candidate was reviewed against a specific head.
/// </summary>
/// <remarks>
/// This records authorization before the candidate is committed. It therefore remains truthful
/// evidence even if a later disk failure prevents that authorized candidate from becoming head.
/// </remarks>
public sealed record LootSpawnPublicationReplacementAuditEntry
{
    public LootSpawnPublicationReplacementAuditEntry(
        LootSpawnPublicationReplacementAuthorization authorization,
        string previousDatasetVersion,
        string replacementDatasetVersion,
        string replacementContentSha256,
        DateTimeOffset recordedUtc)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        PreviousDatasetVersion = LootSpawnItemCatalogEntry.Required(
            previousDatasetVersion,
            nameof(previousDatasetVersion),
            128);
        ReplacementDatasetVersion = LootSpawnItemCatalogEntry.Required(
            replacementDatasetVersion,
            nameof(replacementDatasetVersion),
            128);
        ReplacementContentSha256 = new LootSpawnSourceArtifactIdentity(
            "replacement",
            "reviewed-publication-replacement",
            replacementContentSha256).ContentSha256;
        if (recordedUtc == default || recordedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC audit time is required.", nameof(recordedUtc));
        }

        RecordedUtc = recordedUtc;
    }

    public LootSpawnPublicationReplacementAuthorization Authorization { get; }

    public string PreviousDatasetVersion { get; }

    public string ReplacementDatasetVersion { get; }

    public string ReplacementContentSha256 { get; }

    public DateTimeOffset RecordedUtc { get; }
}

/// <summary>
/// A separate, explicit seam for reviewed removals; ordinary publication never calls this API.
/// </summary>
public interface IReviewedLootSpawnPublicationReplacementStore
{
    ValueTask PublishAuthorizedReplacementAsync(
        LootSpawnSourceBundle bundle,
        LootSpawnPublicationReplacementAuthorization authorization,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<LootSpawnPublicationReplacementAuditEntry>> ReadReplacementAuditAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Refreshes the governed publication head without exposing provider-specific documents.</summary>
public interface ILootSpawnSourceRefreshService
{
    ValueTask<LootSpawnSourceImportResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Parses before publication so a refused source can never displace the last-known-good bundle.</summary>
public sealed class LootSpawnSourceImportService
{
    private readonly ILootSpawnSourceBundleReader _reader;
    private readonly ILootSpawnSourcePublicationStore _store;

    public LootSpawnSourceImportService(
        ILootSpawnSourceBundleReader reader,
        ILootSpawnSourcePublicationStore store)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<LootSpawnSourceImportResult> ImportAsync(
        LootSpawnSourceDocuments documents,
        LootSpawnSourceImportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var candidate = await _reader.ReadAsync(documents, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _store.PublishAsync(candidate, cancellationToken).ConfigureAwait(false);

            var partial = candidate.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "coverage.map-missing", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code, "coverage.map-partial", StringComparison.Ordinal));
            return new(
                partial ? LootSpawnSourceImportDisposition.PublishedPartial : LootSpawnSourceImportDisposition.Published,
                candidate,
                candidate,
                candidate.Diagnostics);
        }
        catch (LootSpawnSourceImportException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = new LootSpawnSourceDiagnostic(exception.Code, exception.Message);
            await _store.QuarantineAsync(diagnostic, context.ImportedUtc, cancellationToken).ConfigureAwait(false);
            var lastKnownGood = await _store.ReadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
            return new(
                LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
                null,
                lastKnownGood,
                [diagnostic]);
        }
    }
}
