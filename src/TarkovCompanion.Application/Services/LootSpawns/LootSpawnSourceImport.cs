using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.LootSpawns;

public sealed record LootSpawnItemCatalogEntry(
    string ItemId,
    string DisplayName,
    string Category,
    long? FleaGrossRoubles,
    long? FleaNetRoubles,
    long? BestTraderRoubles,
    int? OccupiedSquares,
    EvidenceProvenance Provenance);

public sealed record LootSpawnMapSourceDefinition(
    string MapId,
    string TransformVersion,
    MapSceneBounds Bounds,
    IReadOnlySet<string> FloorIds);

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
    public LootSpawnSourceImportContext(
        DateTimeOffset importedUtc,
        TimeSpan maximumSourceAge,
        IReadOnlyDictionary<string, LootSpawnItemCatalogEntry> items,
        IReadOnlyDictionary<string, LootSpawnMapSourceDefinition> maps,
        IReadOnlySet<string> expectedMapIds)
    {
        if (importedUtc == default || importedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC import time is required.", nameof(importedUtc));
        }

        if (maximumSourceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourceAge));
        }

        ImportedUtc = importedUtc;
        MaximumSourceAge = maximumSourceAge;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Maps = maps ?? throw new ArgumentNullException(nameof(maps));
        ExpectedMapIds = expectedMapIds ?? throw new ArgumentNullException(nameof(expectedMapIds));
    }

    public DateTimeOffset ImportedUtc { get; }

    public TimeSpan MaximumSourceAge { get; }

    public IReadOnlyDictionary<string, LootSpawnItemCatalogEntry> Items { get; }

    public IReadOnlyDictionary<string, LootSpawnMapSourceDefinition> Maps { get; }

    public IReadOnlySet<string> ExpectedMapIds { get; }
}

public sealed record LootSpawnMapSourceCoverage(
    string MapId,
    int KnownRecordCount,
    int PublishedRecordCount,
    int PositionedRecordCount,
    int FloorResolvedRecordCount,
    int UnresolvedRecordCount);

public sealed record LootSpawnSourceIdentity(
    int SchemaVersion,
    string DatasetVersion,
    string ContentSha256,
    DateTimeOffset GeneratedUtc,
    DateTimeOffset DataThroughUtc,
    EvidenceSourceClass SourceClass,
    string SourceIdentifier,
    string SourceReference,
    string License,
    EvidenceConfidence Confidence,
    ProducerIdentity Producer);

public sealed record LootSpawnSourceDiagnostic(string Code, string Detail, string? MapId = null);

public sealed record LootSpawnSourceBundle(
    LootSpawnSourceIdentity Identity,
    IReadOnlyList<LootSpawnSnapshot> Snapshots,
    IReadOnlyList<LootSpawnMapSourceCoverage> Coverage,
    IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics);

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
