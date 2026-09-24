using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>
/// #572: reference pictures are described a whole shape at a time, once, and shared by every
/// cell of a scan, and the batch read gives exactly the numbers the one-at-a-time read gave.
/// </summary>
public sealed class IconReferenceShapeDescriptorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "icon-shape-descriptors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheBatchReadDescribesEveryReferenceExactlyAsTheSingleReadDoes()
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        var items = new Items();
        foreach (var seed in Enumerable.Range(1, 5))
        {
            await StoreAsync(cache, $"one-{seed}", Picture(64, 64, seed));
            items.Add($"one-{seed}", 1, 1);
        }

        await StoreAsync(cache, "wide", Picture(127, 64, 9));
        items.Add("wide", 2, 1);

        var batched = await new IconReferenceIndex(cache, items).GetAsync(CancellationToken.None);
        var single = await new IconReferenceIndex(new OneAtATime(cache), items).GetAsync(CancellationToken.None);

        foreach (var (width, height) in new[] { (1, 1), (2, 1) })
        {
            var shaped = batched.OfShape(width, height);
            var described = await batched.DescribeShapeAsync(width, height, CancellationToken.None);
            Assert.Equal(shaped.Count, described.Count);
            for (var index = 0; index < shaped.Count; index++)
            {
                var expected = await single.DescribeAsync(shaped[index], CancellationToken.None);
                Assert.NotNull(expected);
                Assert.NotNull(described[index]);
                Assert.True(expected!.Values.SequenceEqual(described[index]!.Values));
            }
        }

        // The one-at-a-time path answers the same through the same shape call.
        var fallback = await single.DescribeShapeAsync(1, 1, CancellationToken.None);
        Assert.All(fallback, Assert.NotNull);
    }

    [Fact]
    public async Task TheBatchReadLeavesOutAKeyTheCacheDoesNotHold()
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        var held = await StoreAsync(cache, "held", Picture(64, 64, 3));
        var missing = new IconEvidenceKey("missing", new Uri("https://example.invalid/icons/missing.png"));

        var read = await cache.ReadDecodedAsync(
            [held, missing],
            (evidence, image) => $"{evidence.CanonicalItemId} {image.Width}x{image.Height}",
            maximumParallelism: 2,
            CancellationToken.None);

        Assert.Equal("held 64x64", Assert.Single(read).Value);
    }

    [Fact]
    public async Task EveryCellOfAShapeWaitsForOneDecodeOfIt()
    {
        var cache = new CountingCache(("a", 1, 1), ("b", 1, 1), ("c", 1, 1), ("d", 2, 1));
        var snapshot = await new IconReferenceIndex(cache, cache.Items).GetAsync(CancellationToken.None);

        var answers = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => snapshot.DescribeShapeAsync(1, 1, CancellationToken.None)));
        await snapshot.DescribeShapeAsync(1, 1, CancellationToken.None);

        Assert.Equal(1, cache.BatchReads);
        Assert.Equal(3, cache.KeysRead);
        Assert.All(answers, answer => Assert.Equal(3, answer.Count(descriptor => descriptor is not null)));
        // The single read finds the batch's answer and does not go back to the cache.
        Assert.NotNull(await snapshot.DescribeAsync(snapshot.OfShape(1, 1)[0], CancellationToken.None));
        Assert.Equal(0, cache.SingleReads);
    }

    [Fact]
    public async Task WarmingDescribesEveryShapeSoAScanReadsNothing()
    {
        var cache = new CountingCache(("a", 1, 1), ("b", 2, 1), ("c", 2, 2));
        var index = new IconReferenceIndex(cache, cache.Items);

        await index.WarmAsync(CancellationToken.None);
        var snapshot = await index.GetAsync(CancellationToken.None);
        await snapshot.DescribeShapeAsync(2, 2, CancellationToken.None);
        var unknownShape = await snapshot.DescribeShapeAsync(5, 5, CancellationToken.None);

        Assert.Equal(3, cache.BatchReads);
        Assert.Equal(3, cache.KeysRead);
        Assert.Empty(unknownShape);
    }

    [Fact]
    public async Task AFailedShapeReadIsNotKeptAndTheNextScanTriesAgain()
    {
        var cache = new CountingCache(("a", 1, 1), ("b", 1, 1)) { FailNextBatch = true };
        var snapshot = await new IconReferenceIndex(cache, cache.Items).GetAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => snapshot.DescribeShapeAsync(1, 1, CancellationToken.None));
        var retried = await snapshot.DescribeShapeAsync(1, 1, CancellationToken.None);

        Assert.Equal(2, cache.BatchReads);
        Assert.All(retried, Assert.NotNull);
    }

    [Fact]
    public async Task AScanThatIsCancelledStopsWaitingWithoutSpoilingTheSharedRead()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new CountingCache(("a", 1, 1), ("b", 1, 1)) { Gate = gate.Task };
        var snapshot = await new IconReferenceIndex(cache, cache.Items).GetAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();

        var waiting = snapshot.DescribeShapeAsync(1, 1, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.SetResult();
        var answer = await snapshot.DescribeShapeAsync(1, 1, CancellationToken.None);

        Assert.Equal(1, cache.BatchReads);
        Assert.All(answer, Assert.NotNull);
    }

    /// <summary>A bordered tile with a seeded blocky pattern, <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static CapturedImage Picture(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(seed);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 34;
            pixels[offset + 1] = 32;
            pixels[offset + 2] = 30;
            pixels[offset + 3] = 255;
        }

        for (var block = 0; block < 14; block++)
        {
            var left = random.Next(2, width - 14);
            var top = random.Next(2, height - 14);
            var (red, green, blue) = ((byte)random.Next(60, 255), (byte)random.Next(60, 255), (byte)random.Next(60, 255));
            for (var y = top; y < top + 12; y++)
            {
                for (var x = left; x < left + 12; x++)
                {
                    var offset = ((y * width) + x) * 4;
                    pixels[offset] = blue;
                    pixels[offset + 1] = green;
                    pixels[offset + 2] = red;
                }
            }
        }

        return new(pixels, width, height, width * 4, PixelFormat.Bgra8888, Now, "test-picture");
    }

    private static async Task<IconEvidenceKey> StoreAsync(FileIconEvidenceCache cache, string itemId, CapturedImage icon)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(icon.Width, icon.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(icon.Pixels.ToArray(), 0, bitmap.GetPixels(), icon.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var key = new IconEvidenceKey(itemId, new Uri($"https://example.invalid/icons/{itemId}.png"));
        await cache.StoreAsync(
            new IconContentWriteRequest(
                key,
                Now,
                new EvidenceProvenance(
                    EvidenceSourceClass.PublicStructuredData,
                    "fixture://icons",
                    Now,
                    EvidenceConfidence.Certain,
                    new ProducerIdentity("fixture", "1")),
                data.ToArray()),
            CancellationToken.None);
        return key;
    }

    /// <summary>The real cache with its batch read hidden: the path any other cache takes.</summary>
    private sealed class OneAtATime(IIconEvidenceCache inner) : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            inner.StoreAsync(request, cancellationToken);

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            inner.GetAsync(key, cancellationToken);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            inner.ListEvidenceAsync(cancellationToken);
    }

    /// <summary>An in-memory batch reader that counts what it is asked for.</summary>
    private sealed class CountingCache : IIconEvidenceCache, IIconEvidenceBatchReader
    {
        private readonly Dictionary<IconEvidenceKey, (IconContentEvidence Evidence, CapturedImage Image)> _entries = new();
        private int _batchReads;
        private int _keysRead;
        private int _singleReads;

        public CountingCache(params (string Id, int Width, int Height)[] icons)
        {
            var seed = 1;
            foreach (var (id, width, height) in icons)
            {
                var key = new IconEvidenceKey(id, new Uri($"https://example.invalid/icons/{id}.png"));
                var evidence = new IconContentEvidence(
                    key,
                    Now,
                    new string('0', 64),
                    new EvidenceProvenance(EvidenceSourceClass.PublicStructuredData, "fixture://icons", Now, EvidenceConfidence.Certain, new ProducerIdentity("fixture", "1")),
                    new IconPixelDimensions(width * 64, height * 64),
                    new IconFingerprintEvidence(
                        IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
                        IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
                        IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
                        (ulong)seed));
                _entries[key] = (evidence, Picture(width * 64, height * 64, seed++));
                Items.Add(id, width, height);
            }
        }

        public Items Items { get; } = new();

        public bool FailNextBatch { get; set; }

        public Task? Gate { get; init; }

        public int BatchReads => Volatile.Read(ref _batchReads);

        public int KeysRead => Volatile.Read(ref _keysRead);

        public int SingleReads => Volatile.Read(ref _singleReads);

        public async Task<IReadOnlyDictionary<IconEvidenceKey, T>> ReadDecodedAsync<T>(
            IReadOnlyList<IconEvidenceKey> keys,
            Func<IconContentEvidence, CapturedImage, T?> project,
            int maximumParallelism,
            CancellationToken cancellationToken)
            where T : class
        {
            Interlocked.Increment(ref _batchReads);
            if (Gate is { } gate)
            {
                await gate;
            }

            if (FailNextBatch)
            {
                FailNextBatch = false;
                throw new IOException("disk went away");
            }

            Interlocked.Add(ref _keysRead, keys.Count);
            var results = new Dictionary<IconEvidenceKey, T>();
            foreach (var key in keys)
            {
                if (_entries.TryGetValue(key, out var entry) && project(entry.Evidence, entry.Image) is { } projected)
                {
                    results[key] = projected;
                }
            }

            return results;
        }

        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _singleReads);
            return Task.FromResult<IconContentEvidenceAsset?>(null);
        }

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconContentEvidence>>(_entries.Values.Select(entry => entry.Evidence).ToArray());
    }

    private sealed class Items : IItemRepository
    {
        private readonly Dictionary<string, ItemDimensions> _dimensions = new(StringComparer.Ordinal);

        public void Add(string id, int width, int height) => _dimensions[id] = new(width, height);

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(_dimensions.TryGetValue(itemId, out var dimensions)
                ? new ItemDefinition(
                    itemId,
                    "Name of " + itemId,
                    itemId,
                    string.Empty,
                    ItemCategory.Barter,
                    dimensions,
                    true,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new HashSet<string>(),
                    new DataProvenance("fixture", Now))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
