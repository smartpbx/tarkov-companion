using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// A cache that can read, validate and decode many icons in one pass. Optional: a cache without
/// it is read one icon at a time.
/// </summary>
public interface IIconEvidenceBatchReader
{
    /// <summary>
    /// Each held and valid key's decoded picture, passed through <paramref name="project"/>
    /// before its pixels are released; keys that are missing, damaged or projected to null are
    /// left out.
    /// </summary>
    Task<IReadOnlyDictionary<IconEvidenceKey, T>> ReadDecodedAsync<T>(
        IReadOnlyList<IconEvidenceKey> keys,
        Func<IconContentEvidence, CapturedImage, T?> project,
        int maximumParallelism,
        CancellationToken cancellationToken)
        where T : class;
}

/// <summary>One reference icon together with the catalog item it depicts.</summary>
public sealed record IconReference(IconContentEvidence Evidence, ItemDefinition Definition);

/// <summary>
/// The reference icons a scan matches against, read once and kept until something changes them.
/// </summary>
/// <remarks>
/// The grid builder used to list the on-disk cache and then fetch every item's definition one
/// query at a time, on every scan. With the full catalog indexed that is 5,320 documents
/// re-hashed and re-decoded (7.8 s measured) and 5,320 item lookups before the first cell is
/// looked at, for an answer that only changes when the catalog syncs. Pixel descriptors are kept
/// for the same reason, one decode per icon per catalog load: every reference of a shape is
/// compared (see the builder), so they are built a whole shape at a time, and
/// <see cref="WarmAsync"/> builds them all in the background once the icon index is refreshed,
/// before a scan has to wait for them (about 46 MB for the 5,320 icons of 2026-09).
/// </remarks>
public sealed class IconReferenceIndex(IIconEvidenceCache cache, IItemRepository items)
{
    private readonly IIconEvidenceCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    public async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _snapshot) is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is { } raced)
            {
                return raced;
            }

            var evidence = await _cache.ListEvidenceAsync(cancellationToken).ConfigureAwait(false);
            var definitions = new Dictionary<string, ItemDefinition?>(StringComparer.Ordinal);
            var references = new List<IconReference>(evidence.Count);
            foreach (var entry in evidence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!definitions.TryGetValue(entry.CanonicalItemId, out var definition))
                {
                    definition = await _items.GetAsync(entry.CanonicalItemId, cancellationToken).ConfigureAwait(false);
                    definitions[entry.CanonicalItemId] = definition;
                }

                if (definition is not null)
                {
                    references.Add(new(entry, definition));
                }
            }

            var built = new Snapshot(_cache, references);
            // An empty index is not worth keeping: on a fresh install the icons arrive minutes
            // after the first scan could, and a kept empty answer would outlive them.
            if (references.Count > 0)
            {
                Volatile.Write(ref _snapshot, built);
            }

            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the kept answer, so the next scan sees icons stored since.</summary>
    public void Invalidate() => Volatile.Write(ref _snapshot, null);

    /// <summary>
    /// Builds the snapshot, if it is not held, and describes every reference in it, so the first
    /// scan of a session finds the work done. Called in the background after the icon index is
    /// refreshed; a scan that arrives first shares the same per-shape work rather than repeating it.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(cancellationToken).ConfigureAwait(false);
        foreach (var shape in snapshot.Shapes)
        {
            await snapshot.DescribeShapeAsync(shape.Width, shape.Height, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class Snapshot
    {
        private readonly IIconEvidenceCache _cache;
        private readonly ConcurrentDictionary<IconEvidenceKey, IconPixelDescriptor?> _descriptors = new();
        private readonly ConcurrentDictionary<(int Width, int Height), Lazy<Task<IconPixelDescriptor?[]>>> _shapeDescriptors = new();
        private readonly Dictionary<(int Width, int Height), IconReference[]> _byShape;

        internal Snapshot(IIconEvidenceCache cache, IReadOnlyList<IconReference> references)
        {
            _cache = cache;
            References = references;
            _byShape = references
                .GroupBy(reference => (reference.Definition.Dimensions.Width, reference.Definition.Dimensions.Height))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public IReadOnlyList<IconReference> References { get; }

        /// <summary>Every footprint shape at least one reference is drawn at, unrotated.</summary>
        public IEnumerable<(int Width, int Height)> Shapes => _byShape.Keys;

        /// <summary>
        /// How many threads may decode reference icons at once: half the machine, so a scan or a
        /// warm-up never takes every core from the game running beside it.
        /// </summary>
        public static int DecodeParallelism => Math.Max(1, Environment.ProcessorCount / 2);

        /// <summary>References drawn at exactly this many cells, unrotated.</summary>
        public IReadOnlyList<IconReference> OfShape(int widthCells, int heightCells) =>
            _byShape.TryGetValue((widthCells, heightCells), out var references) ? references : [];

        /// <summary>
        /// The pixel descriptor of every reference of this shape, index for index with
        /// <see cref="OfShape"/>; null where a reference's bytes cannot be read.
        /// </summary>
        /// <remarks>
        /// #572: every cell of a grid used to ask for each of its shape's references one at a
        /// time, and the cells of a grid ask at once, so on the first scan of a session eight
        /// cells each decoded the same 2,480 one-cell icons, queued on the cache's exclusive lease.
        /// One real 3840x1080 loot frame took 57 s on dev that way. Now the first cell to ask
        /// starts one decode of the whole shape and every other cell waits for that same answer.
        /// The shared work is not cancelled with the scan that started it: it is bounded, and the
        /// next scan wants the same answer.
        /// </remarks>
        public async Task<IReadOnlyList<IconPixelDescriptor?>> DescribeShapeAsync(
            int widthCells,
            int heightCells,
            CancellationToken cancellationToken)
        {
            var shape = (widthCells, heightCells);
            if (!_byShape.ContainsKey(shape))
            {
                return [];
            }

            var lazy = _shapeDescriptors.GetOrAdd(
                shape,
                key => new Lazy<Task<IconPixelDescriptor?[]>>(
                    () => Task.Run(() => BuildShapeAsync(_byShape[key])),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
            {
                // A failed read is not kept: the next scan tries again.
                _shapeDescriptors.TryRemove(new KeyValuePair<(int Width, int Height), Lazy<Task<IconPixelDescriptor?[]>>>(shape, lazy));
                throw;
            }
        }

        private async Task<IconPixelDescriptor?[]> BuildShapeAsync(IconReference[] shaped)
        {
            var missing = shaped
                .Where(reference => !_descriptors.ContainsKey(reference.Evidence.Key))
                .ToArray();
            if (missing.Length > 0 && _cache is IIconEvidenceBatchReader batch)
            {
                var dimensions = missing.ToDictionary(reference => reference.Evidence.Key, reference => reference.Definition.Dimensions);
                var read = await batch.ReadDecodedAsync(
                        missing.Select(reference => reference.Evidence.Key).ToArray(),
                        (evidence, image) => dimensions.TryGetValue(evidence.Key, out var cells)
                            ? IconPixelDescriptor.Create(image, cells.Width, cells.Height)
                            : null,
                        DecodeParallelism,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                foreach (var reference in missing)
                {
                    _descriptors[reference.Evidence.Key] = read.TryGetValue(reference.Evidence.Key, out var descriptor) ? descriptor : null;
                }
            }
            else if (missing.Length > 0)
            {
                await Parallel.ForEachAsync(
                        missing,
                        new ParallelOptions { MaxDegreeOfParallelism = DecodeParallelism },
                        async (reference, token) => await DescribeAsync(reference, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }

            return shaped
                .Select(reference => _descriptors.TryGetValue(reference.Evidence.Key, out var descriptor) ? descriptor : null)
                .ToArray();
        }

        /// <summary>The reference's pixel descriptor, or null when its bytes cannot be read.</summary>
        public async Task<IconPixelDescriptor?> DescribeAsync(IconReference reference, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (_descriptors.TryGetValue(reference.Evidence.Key, out var known))
            {
                return known;
            }

            var asset = await _cache.GetAsync(reference.Evidence.Key, cancellationToken).ConfigureAwait(false);
            var descriptor = asset is null
                ? null
                : Describe(asset.Content, reference.Definition.Dimensions);
            _descriptors[reference.Evidence.Key] = descriptor;
            return descriptor;
        }

        private static IconPixelDescriptor? Describe(ReadOnlyMemory<byte> content, ItemDimensions dimensions)
        {
            // The cache already bounded these bytes and their decoded size when it stored them.
            using var decoded = SKBitmap.Decode(content.Span);
            if (decoded is null)
            {
                return null;
            }

            using var bitmap = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
            var source = bitmap ?? decoded;
            var pixels = new byte[checked(source.RowBytes * source.Height)];
            Marshal.Copy(source.GetPixels(), pixels, 0, pixels.Length);
            var image = new CapturedImage(
                pixels,
                source.Width,
                source.Height,
                source.RowBytes,
                PixelFormat.Bgra8888,
                DateTimeOffset.UnixEpoch,
                "reference-icon");
            return IconPixelDescriptor.Create(image, dimensions.Width, dimensions.Height);
        }
    }
}
