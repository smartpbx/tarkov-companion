using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>One reference icon together with the catalog item it depicts.</summary>
public sealed record IconReference(IconContentEvidence Evidence, ItemDefinition Definition);

/// <summary>
/// The reference icons a scan matches against, read once and kept until something changes them.
/// </summary>
/// <remarks>
/// The grid builder used to list the on-disk cache and then fetch every item's definition one
/// query at a time, on every scan. With the full catalog indexed that is 5,320 documents
/// re-hashed and re-decoded (7.8 s measured) and 5,320 item lookups before the first cell is
/// looked at, for an answer that only changes when the catalog syncs. Pixel descriptors are
/// built on first use for the same reason: a scan only ever needs the few dozen its shortlists
/// name, and decoding all of them up front would cost the first scan of a session seconds.
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

    public sealed class Snapshot
    {
        private readonly IIconEvidenceCache _cache;
        private readonly ConcurrentDictionary<IconEvidenceKey, IconPixelDescriptor?> _descriptors = new();
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

        /// <summary>References drawn at exactly this many cells, unrotated.</summary>
        public IReadOnlyList<IconReference> OfShape(int widthCells, int heightCells) =>
            _byShape.TryGetValue((widthCells, heightCells), out var references) ? references : [];

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
