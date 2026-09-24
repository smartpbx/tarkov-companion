using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>Opens (and on first use fills) a real on-disk icon index built from the corpus.</summary>
internal static class LootScanMeasurementIndex
{
    public static string DirectoryFor(IconCorpus corpus) =>
        Path.Combine(Path.GetDirectoryName(corpus.Directory.TrimEnd(Path.DirectorySeparatorChar))!, "icon-evidence-cache");

    public static async Task<IIconEvidenceCache> OpenAsync(IconCorpus corpus, DateTimeOffset nowUtc)
    {
        var directory = DirectoryFor(corpus);
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(directory) { MaximumEntries = 8192 });
        var present = (await cache.ListEvidenceAsync(CancellationToken.None))
            .Select(entry => entry.CanonicalItemId)
            .ToHashSet(StringComparer.Ordinal);
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "https://assets.tarkov.dev/",
            nowUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("loot-scan-measurement", "1"));
        foreach (var item in corpus.Items.Where(item => !present.Contains(item.Id)))
        {
            try
            {
                await cache.StoreAsync(
                    new IconContentWriteRequest(new IconEvidenceKey(item.Id, item.ImageUri), nowUtc, provenance, corpus.ReadIcon(item.Id)),
                    CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                // An icon the cache refuses to decode is simply not indexed.
            }
        }

        return new SnapshotCache(cache, await cache.ListEvidenceAsync(CancellationToken.None));
    }

    /// <summary>Lists once. The on-disk cache re-decodes every document on every listing.</summary>
    private sealed class SnapshotCache(IIconEvidenceCache inner, IReadOnlyList<IconContentEvidence> evidence)
        : IIconEvidenceCache, IIconEvidenceBatchReader
    {
        public Task<IReadOnlyDictionary<IconEvidenceKey, T>> ReadDecodedAsync<T>(
            IReadOnlyList<IconEvidenceKey> keys,
            Func<IconContentEvidence, CapturedImage, T?> project,
            int maximumParallelism,
            CancellationToken cancellationToken)
            where T : class =>
            ((IIconEvidenceBatchReader)inner).ReadDecodedAsync(keys, project, maximumParallelism, cancellationToken);

        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            inner.GetAsync(key, cancellationToken);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(evidence);
    }
}

internal static class LootScanMeasurementStages
{
    public static IEnumerable<MeasurementStage> All(
        IconCorpus corpus,
        IIconEvidenceCache index,
        ProfileRuntimeContextService runtime,
        DateTimeOffset nowUtc)
    {
        var clock = new ManualTimeProvider(nowUtc);
        var items = corpus.CreateRepository(nowUtc);
        var ocr = new NoOcrEngine();

        yield return new(
            "empty icon index (what the app has today)",
            new(
                new GridPixelReconstructionBuilder(new EmptyCache(), items, ocr),
                new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService(clock), clock)),
            _ => null);

        yield return new(
            "full icon index",
            new(
                new GridPixelReconstructionBuilder(index, items, ocr),
                new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService(clock), clock)),
            _ => null);

        yield return new(
            "full icon index and catalog recommendations (what this branch ships)",
            new(
                new GridPixelReconstructionBuilder(index, items, ocr),
                new LootScanCaptureHandoff(
                    runtime,
                    new InventoryGridReconstructor(),
                    new LootScanDecisionService(clock),
                    clock,
                    recommendations: new LootScanRecommendationSource(items))),
            PersonOracle);
    }

    /// <summary>
    /// What a player would do with the true item on value alone: take it at 10,000 roubles a
    /// square or better, by the better of the flea average and the best trader, else leave it.
    /// </summary>
    private static TarkovCompanion.Core.Domain.Loot.LootScanVerdict? PersonOracle(CorpusItem item)
    {
        var value = Math.Max(item.FleaEligible ? item.Average24HourRoubles ?? 0 : 0, item.BestTraderRoubles ?? 0);
        if (value <= 0)
        {
            return null;
        }

        return value / (item.Width * item.Height) >= 10_000
            ? TarkovCompanion.Core.Domain.Loot.LootScanVerdict.Take
            : TarkovCompanion.Core.Domain.Loot.LootScanVerdict.Leave;
    }

    private sealed class EmptyCache : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconContentEvidence>>([]);
    }

    /// <summary>Tesseract is not installed where this runs, so stack counts stay unread.</summary>
    private sealed class NoOcrEngine : TarkovCompanion.Core.Abstractions.IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "loot-scan-measurement", IsAvailable: false, DiagnosticCode: "ocr_not_installed"));
    }
}
