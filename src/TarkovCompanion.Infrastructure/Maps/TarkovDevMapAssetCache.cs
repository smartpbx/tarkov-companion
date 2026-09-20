using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

public sealed record MapAssetCacheOptions(
    string CacheDirectory,
    TimeSpan RefreshAfter,
    TimeSpan RequestTimeout,
    long MaximumAssetBytes)
{
    /// <remarks>
    /// One photographed map is 170 to 240 tiles, so the 512 this used to be held two maps and a
    /// bit: opening a third evicted the first, and going back to it downloaded it again (measured
    /// on 2026-09-20: Customs, 0.9 s from disk, took 41 s after one visit to Reserve). Thirteen maps
    /// and their floors are a few thousand tiles of about 35 KB each, well inside the byte bound,
    /// which is the bound that actually protects the disk.
    /// </remarks>
    public int MaximumCacheEntries { get; init; } = 8192;

    public long MaximumCacheBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// How to rasterise a drawing in a child process, or null to do it in this one.
    /// </summary>
    /// <remarks>
    /// Null everywhere except the running application. Rasterising a drawing killed the process
    /// outright on 2026-09-19 — a native access violation inside Skia, which no managed handler
    /// can see — and a child process is the only arrangement that survives one. Tests and tools
    /// leave this unset and rasterise in process, which is both simpler to reason about and what
    /// they were already doing.
    /// </remarks>
    public SvgRasterizerHost? Rasterizer { get; init; }

    public static MapAssetCacheOptions CreateDefault(string cacheDirectory) => new(
        cacheDirectory,
        TimeSpan.FromDays(30),
        TimeSpan.FromSeconds(20),
        32L * 1024 * 1024);
}

public sealed record CachedMapAsset(
    Uri SourceUri,
    string LocalPath,
    string RenderPath,
    string ContentSha256,
    DateTimeOffset RetrievedUtc,
    string? Author,
    Uri? AuthorLink,
    string LicenseIdentifier,
    Uri LicenseUri,
    MapAssetAvailability Availability);

public sealed record MapAssetCacheResult(CachedMapAsset? Asset, string? Message)
{
    public bool IsAvailable => Asset is not null;
}

public sealed class TarkovDevMapAssetCache(
    HttpClient httpClient,
    MapAssetCacheOptions options,
    TimeProvider? timeProvider = null)
{
    public const string LicenseIdentifier = "CC-BY-NC-SA-4.0";
    public static readonly Uri LicenseUri = new("https://creativecommons.org/licenses/by-nc-sa/4.0/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "assets.tarkov.dev",
        "tarkov.dev",
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _entryGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);

    // What the last full scan of the cache directory found, plus what has been written since.
    // Null until a scan has run. Guarded by _accountingLock.
    private readonly object _accountingLock = new();
    private long? _knownEntries;
    private long _knownBytes;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<MapAssetCacheResult> GetSvgAsync(MapVariant variant, CancellationToken cancellationToken) =>
        GetSvgAsync(variant, variant.Floors.FirstOrDefault(floor => floor.IsVisibleByDefault), cancellationToken);

    public async Task<MapAssetCacheResult> GetSvgAsync(
        MapVariant variant,
        MapFloorDefinition? floor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant.SvgPath is null)
        {
            return new(null, "This variant has no SVG asset.");
        }

        var result = await GetAsync(variant.SvgPath, variant.Author, variant.AuthorLink, cancellationToken)
            .ConfigureAwait(false);
        if (result.Asset is null)
        {
            return result;
        }

        var isBaseFloor = floor is null || string.Equals(floor.Id, "base", StringComparison.Ordinal);
        var visibleLayer = isBaseFloor ? floor?.SvgLayer ?? variant.SvgLayer : floor!.SvgLayer;
        if (!isBaseFloor && string.IsNullOrWhiteSpace(visibleLayer))
        {
            return new(null, $"Floor '{floor!.Name}' has no explicit upstream SVG layer.");
        }

        if (string.IsNullOrWhiteSpace(visibleLayer))
        {
            return result;
        }

        // One file per layer, not one file per asset. Every floor used to rasterise onto the one
        // <hash>.preview.png, so two consumers reading two floors of the same map — the V1 map
        // and the V2 cockpit both hold this cache — each got whichever floor had finished last.
        // RaidCockpitViewModel carried a remark about exactly that; this removes the cause.
        var layerPreviewPath = GetLayerPreviewPath(variant.SvgPath, visibleLayer!);
        var layerAsset = result.Asset with { RenderPath = layerPreviewPath };
        var renderGate = _entryGates.GetOrAdd(
            variant.SvgPath.AbsoluteUri + "#preview:" + visibleLayer,
            static _ => new(1, 1));
        await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Rasterised once, then never again while it is current. A stacked multi-floor map
            // asks for every floor in turn on every load, and Reserve's six floors were six full
            // 4096-wide renders each time — seconds of work per load for a picture already on
            // disk, and six more chances for the native fault this is guarding against.
            if (!IsCurrent(layerPreviewPath, result.Asset.LocalPath))
            {
                var temporaryPath = layerPreviewPath + ".tmp";
                try
                {
                    await RasterizeAsync(result.Asset.LocalPath, temporaryPath, visibleLayer, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryPath, layerPreviewPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            return new(layerAsset, $"{result.Message} Showing upstream SVG layer '{visibleLayer}'.");
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            // The whole drawing, if it is there, rather than nothing. A floor that will not
            // rasterise is a worse map, not an absent one, and the base preview was produced by
            // the download and is the same artwork with every floor drawn.
            return File.Exists(result.Asset.RenderPath)
                ? new(result.Asset, $"The '{visibleLayer}' floor could not be drawn, so every floor is shown: {exception.Message}")
                : new(null, $"The selected SVG floor is unavailable: {exception.Message}");
        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <summary>
    /// Rasterises in a child process where one is configured, and in this one where it is not.
    /// </summary>
    /// <remarks>
    /// A child that could not be *started* says nothing about the drawing, so this falls back to
    /// rasterising here: the alternative would be a map that stops working because of how the
    /// application happens to be launched. A child that started and died says the opposite, and
    /// its exception is left to propagate — retrying it in this process is how the application
    /// would be killed by the fault the child was there to contain.
    /// </remarks>
    private async Task RasterizeAsync(
        string svgPath,
        string previewPath,
        string? visibleLayer,
        CancellationToken cancellationToken)
    {
        if (options.Rasterizer is { } host)
        {
            try
            {
                await OutOfProcessSvgRasterizer
                    .CreatePreviewAsync(host, svgPath, previewPath, visibleLayer, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (SvgRasterizerHostUnavailableException)
            {
            }
        }

        await SvgMapRasterizer
            .CreatePreviewAsync(svgPath, previewPath, visibleLayer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Whether a preview already on disk was drawn from the SVG that is there now.</summary>
    /// <remarks>
    /// Compared by write time rather than by content hash because the source is replaced whole by
    /// <see cref="DownloadAsync"/> and never edited in place, and because re-hashing a
    /// multi-megabyte SVG to decide whether to skip work is most of the work.
    /// </remarks>
    private static bool IsCurrent(string previewPath, string svgPath)
    {
        try
        {
            var preview = new FileInfo(previewPath);
            var svg = new FileInfo(svgPath);
            return preview.Exists && preview.Length > 0 && svg.Exists && preview.LastWriteTimeUtc >= svg.LastWriteTimeUtc;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where the preview of one upstream layer of one asset lives.</summary>
    /// <remarks>
    /// The readable part of the layer name is kept so the cache directory can be understood by
    /// looking at it, and a hash of the whole name is appended because upstream layer ids are not
    /// filenames: they carry spaces and punctuation, and two different ids must never reduce to
    /// one file.
    /// </remarks>
    private string GetLayerPreviewPath(Uri sourceUri, string visibleLayer)
    {
        var readable = new string([.. visibleLayer
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .Take(32)]);
        var distinct = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(visibleLayer)))[..8];
        return Path.Combine(
            options.CacheDirectory,
            $"{GetCacheKey(sourceUri)}.preview.{readable}-{distinct}.png");
    }

    public Task<MapAssetCacheResult> GetTileAsync(
        MapVariant variant,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant.TilePath is null)
        {
            return Task.FromResult(new MapAssetCacheResult(null, "This variant has no PNG tile asset."));
        }

        if (zoom < variant.MinimumZoom || zoom > variant.MaximumZoom)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "The tile coordinate is outside the variant's configured zoom range.");
        }

        var template = Uri.UnescapeDataString(variant.TilePath.AbsoluteUri);
        var resolved = template
            .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var tileUri))
        {
            throw new InvalidDataException("The configured tile template did not produce a valid URI.");
        }

        return GetAsync(tileUri, variant.Author, variant.AuthorLink, cancellationToken);
    }

    public async Task<MapAssetCacheResult> GetAsync(
        Uri sourceUri,
        string? author,
        Uri? authorLink,
        CancellationToken cancellationToken)
    {
        ValidateOptions();
        ValidateSource(sourceUri);
        var entryGate = _entryGates.GetOrAdd(sourceUri.AbsoluteUri, static _ => new(1, 1));
        await entryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssetCacheDocument? cached = null;
            string? cacheError = null;
            try
            {
                cached = await ReadMetadataAsync(sourceUri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                cacheError = exception.Message;
            }

            var now = _timeProvider.GetUtcNow();
            if (cached is not null && now - cached.RetrievedUtc <= options.RefreshAfter)
            {
                return new(ToAsset(cached, MapAssetAvailability.Available), "Using the current local map asset cache.");
            }

            try
            {
                return await DownloadAsync(sourceUri, author, authorLink, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                return cached is not null
                    ? new(ToAsset(cached, MapAssetAvailability.CachedOffline), $"Asset refresh failed; using offline cache: {exception.Message}")
                    : new(
                        null,
                        cacheError is null
                            ? $"Map asset is unavailable: {exception.Message}"
                            : $"Map asset and local cache are unavailable: {exception.Message}; cache: {cacheError}");
            }
        }
        finally
        {
            entryGate.Release();
        }
    }

    private async Task<MapAssetCacheResult> DownloadAsync(
        Uri sourceUri,
        string? author,
        Uri? authorLink,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TarkovCompanion", "1.0"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > options.MaximumAssetBytes)
        {
            throw new InvalidDataException("The map asset exceeds its size limit.");
        }

        Directory.CreateDirectory(options.CacheDirectory);
        var cacheKey = GetCacheKey(sourceUri);
        var extension = GetExtension(sourceUri, response.Content.Headers.ContentType?.MediaType);
        var fileName = cacheKey + extension;
        var localPath = Path.Combine(options.CacheDirectory, fileName);
        var temporaryPath = localPath + ".tmp";
        string contentHash;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                contentHash = await CopyBoundedAndHashAsync(input, output, options.MaximumAssetBytes, timeout.Token).ConfigureAwait(false);
            }

            File.Move(temporaryPath, localPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        string? renderFileName = null;
        if (string.Equals(extension, ".svg", StringComparison.Ordinal))
        {
            renderFileName = cacheKey + ".preview.png";
            var previewPath = Path.Combine(options.CacheDirectory, renderFileName);
            var temporaryPreviewPath = previewPath + ".tmp";
            try
            {
                await RasterizeAsync(localPath, temporaryPreviewPath, visibleLayer: null, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPreviewPath, previewPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPreviewPath))
                {
                    File.Delete(temporaryPreviewPath);
                }
            }
        }

        // Drawn from a file that has just been replaced, so no longer of this asset. Left
        // standing they would be served for the life of the cache entry: IsCurrent compares write
        // times, and a preview written after the new SVG landed would pass.
        foreach (var stale in EnumerateLayerPreviews(cacheKey))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Still in use by a reader. It will be redrawn on the next request that finds it
                // older than the source.
            }
        }

        var metadata = new AssetCacheDocument(
            sourceUri.AbsoluteUri,
            fileName,
            renderFileName,
            contentHash,
            now,
            author,
            authorLink?.AbsoluteUri,
            LicenseIdentifier,
            LicenseUri.AbsoluteUri);
        var isNewEntry = !File.Exists(GetMetadataPath(sourceUri));
        await WriteMetadataAsync(sourceUri, metadata, cancellationToken).ConfigureAwait(false);
        // A drawing brings a rasterised preview and later one more per floor, none of which the
        // running totals see, so a drawing always gets the full scan; there is one per map.
        if (renderFileName is not null || !StaysWithinBounds(isNewEntry, new FileInfo(localPath).Length))
        {
            await EnforceCacheBoundsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }

        return new(ToAsset(metadata, MapAssetAvailability.Available), "Map asset downloaded from tarkov.dev.");
    }

    private async Task<AssetCacheDocument?> ReadMetadataAsync(Uri sourceUri, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(sourceUri);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 32 * 1024)
        {
            throw new InvalidDataException("The local map asset metadata exceeds its size limit.");
        }

        var metadata = await JsonSerializer
            .DeserializeAsync<AssetCacheDocument>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null ||
            !string.Equals(metadata.SourceUri, sourceUri.AbsoluteUri, StringComparison.Ordinal) ||
            !string.Equals(metadata.LicenseIdentifier, LicenseIdentifier, StringComparison.Ordinal) ||
            !string.Equals(metadata.LicenseUri, LicenseUri.AbsoluteUri, StringComparison.Ordinal) ||
            metadata.ContentSha256.Length != 64 ||
            string.IsNullOrWhiteSpace(metadata.FileName) ||
            Path.GetFileName(metadata.FileName) != metadata.FileName ||
            metadata.RenderFileName is not null && Path.GetFileName(metadata.RenderFileName) != metadata.RenderFileName)
        {
            throw new InvalidDataException("The local map asset metadata is invalid.");
        }

        var localPath = Path.Combine(options.CacheDirectory, metadata.FileName);
        if (!File.Exists(localPath) || new FileInfo(localPath).Length > options.MaximumAssetBytes)
        {
            return null;
        }

        if (metadata.RenderFileName is not null && !File.Exists(Path.Combine(options.CacheDirectory, metadata.RenderFileName)))
        {
            return null;
        }

        await using var assetStream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(assetStream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(contentHash, metadata.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local map asset failed its content hash check.");
        }

        return metadata;
    }

    private async Task WriteMetadataAsync(
        Uri sourceUri,
        AssetCacheDocument metadata,
        CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(sourceUri);
        var temporaryPath = metadataPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, metadataPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private CachedMapAsset ToAsset(AssetCacheDocument metadata, MapAssetAvailability availability) => new(
        new(metadata.SourceUri),
        Path.Combine(options.CacheDirectory, metadata.FileName),
        Path.Combine(options.CacheDirectory, metadata.RenderFileName ?? metadata.FileName),
        metadata.ContentSha256,
        metadata.RetrievedUtc,
        metadata.Author,
        Uri.TryCreate(metadata.AuthorLink, UriKind.Absolute, out var authorLink) ? authorLink : null,
        metadata.LicenseIdentifier,
        new(metadata.LicenseUri),
        availability);

    private string GetMetadataPath(Uri sourceUri) => Path.Combine(options.CacheDirectory, GetCacheKey(sourceUri) + ".metadata.json");

    /// <summary>
    /// Adds one written asset to the running totals and says whether the cache is known to be inside its bounds.
    /// </summary>
    /// <remarks>
    /// The bounds used to be enforced by reading every metadata file in the cache after every
    /// download, one download at a time. A map is some two hundred downloads, so opening one read
    /// the directory two hundred times over: measured on 2026-09-20, a first visit to Reserve took
    /// 53.6 s of which the four concurrent downloads spent 195 s (summed) in that scan and 15 s on
    /// the network and the disk. The totals make the common case, a cache nowhere near its bounds,
    /// cost nothing; the scan still runs, and still evicts exactly, once they say a bound is near.
    /// A replaced asset is counted as added, which can only make the scan run early, and the scan
    /// puts the totals right.
    /// </remarks>
    private bool StaysWithinBounds(bool isNewEntry, long sizeBytes)
    {
        lock (_accountingLock)
        {
            if (_knownEntries is not { } entries)
            {
                return false;
            }

            _knownEntries = entries + (isNewEntry ? 1 : 0);
            _knownBytes += sizeBytes;
            return _knownEntries <= options.MaximumCacheEntries && _knownBytes <= options.MaximumCacheBytes;
        }
    }

    private async Task EnforceCacheBoundsAsync(string protectedCacheKey, CancellationToken cancellationToken)
    {
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = new List<CacheEntry>();
            foreach (var metadataPath in Directory.EnumerateFiles(options.CacheDirectory, "*.metadata.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(
                        metadataPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length > 32 * 1024)
                    {
                        continue;
                    }

                    var metadata = await JsonSerializer
                        .DeserializeAsync<AssetCacheDocument>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    if (metadata is null ||
                        string.IsNullOrWhiteSpace(metadata.FileName) ||
                        Path.GetFileName(metadata.FileName) != metadata.FileName ||
                        metadata.RenderFileName is not null &&
                        Path.GetFileName(metadata.RenderFileName) != metadata.RenderFileName)
                    {
                        continue;
                    }

                    var cacheKey = Path.GetFileName(metadataPath)[..^".metadata.json".Length];
                    // The per-layer previews belong to this entry as much as the base one does.
                    // Counted here or a six-floor map's previews are half a gigabyte the bound
                    // knows nothing about, and evicted with it or they outlive the entry that
                    // explains them.
                    var paths = new[]
                        {
                            metadataPath,
                            Path.Combine(options.CacheDirectory, metadata.FileName),
                            metadata.RenderFileName is null
                                ? null
                                : Path.Combine(options.CacheDirectory, metadata.RenderFileName),
                        }
                        .Concat(EnumerateLayerPreviews(cacheKey))
                        .Where(path => path is not null && File.Exists(path))
                        .Select(path => path!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    entries.Add(new(
                        cacheKey,
                        metadata.RetrievedUtc,
                        paths,
                        paths.Sum(path => new FileInfo(path).Length)));
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    // A malformed entry is handled as unavailable by its normal read path.
                }
            }

            var protectedEntry = entries.FirstOrDefault(entry =>
                string.Equals(entry.CacheKey, protectedCacheKey, StringComparison.Ordinal));
            if (protectedEntry is not null && protectedEntry.SizeBytes > options.MaximumCacheBytes)
            {
                TryDeleteEntry(protectedEntry);
                throw new InvalidDataException("The rendered map asset exceeds the total cache size limit.");
            }

            var totalBytes = entries.Sum(entry => entry.SizeBytes);
            var entryCount = entries.Count;
            // Once a bound is crossed, evict to an eighth below it rather than to the bound itself,
            // so a cache that is full scans once per several hundred downloads and not once per
            // download. A small bound has no eighth to give and is kept exactly.
            var overBounds = entryCount > options.MaximumCacheEntries || totalBytes > options.MaximumCacheBytes;
            var entryTarget = options.MaximumCacheEntries - (options.MaximumCacheEntries / 8);
            var byteTarget = options.MaximumCacheBytes - (options.MaximumCacheBytes / 8);
            foreach (var entry in entries
                         .Where(entry => !string.Equals(entry.CacheKey, protectedCacheKey, StringComparison.Ordinal))
                         .OrderBy(entry => entry.RetrievedUtc))
            {
                if (!overBounds || (entryCount <= entryTarget && totalBytes <= byteTarget))
                {
                    break;
                }

                if (TryDeleteEntry(entry))
                {
                    entryCount--;
                    totalBytes -= entry.SizeBytes;
                }
            }

            lock (_accountingLock)
            {
                _knownEntries = entryCount;
                _knownBytes = totalBytes;
            }
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    private static bool TryDeleteEntry(CacheEntry entry)
    {
        var metadataPath = entry.Paths.FirstOrDefault(path =>
            path.EndsWith(".metadata.json", StringComparison.Ordinal));
        foreach (var path in entry.Paths.Where(path => !string.Equals(path, metadataPath, StringComparison.Ordinal)))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A file currently in use remains eligible for the next bounded-cache pass.
                return false;
            }
        }

        if (metadataPath is not null)
        {
            try
            {
                File.Delete(metadataPath);
            }
            catch (IOException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every per-layer preview belonging to one cache entry.</summary>
    /// <remarks>
    /// Filtered in code rather than by a search pattern. Windows matches wildcards against short
    /// names as well as long ones, so a pattern narrow enough to exclude the base preview is not
    /// reliably narrow, and this is not a hot path.
    /// </remarks>
    private IEnumerable<string> EnumerateLayerPreviews(string cacheKey)
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(options.CacheDirectory, cacheKey + ".preview.*");
        }
        catch (Exception failure) when (failure is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var basePreview = cacheKey + ".preview.png";
        return candidates
            .Where(path => Path.GetFileName(path).EndsWith(".png", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), basePreview, StringComparison.Ordinal))
            .ToArray();
    }

    private static string GetCacheKey(Uri sourceUri) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUri.AbsoluteUri)));

    private static string GetExtension(Uri sourceUri, string? mediaType)
    {
        if (string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
            sourceUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return ".svg";
        }

        if (string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            sourceUri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            sourceUri.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return ".jpg";
        }

        return ".png";
    }

    private static async Task<string> CopyBoundedAndHashAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("The map asset exceeds its size limit.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateOptions()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        if (options.RefreshAfter < TimeSpan.Zero ||
            options.RequestTimeout <= TimeSpan.Zero ||
            options.MaximumAssetBytes <= 0 ||
            options.MaximumCacheEntries <= 0 ||
            options.MaximumCacheBytes <= 0)
        {
            throw new InvalidOperationException("The map asset cache bounds must be positive.");
        }
    }

    private static void ValidateSource(Uri sourceUri)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (!sourceUri.IsAbsoluteUri || sourceUri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(sourceUri.Host))
        {
            throw new InvalidDataException("Map assets must use HTTPS on an approved tarkov.dev host.");
        }
    }

    private static bool IsRecoverable(Exception exception, CancellationToken callerToken) => exception switch
    {
        OperationCanceledException when callerToken.IsCancellationRequested => false,
        HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException => true,
        _ => false,
    };

    private sealed record AssetCacheDocument(
        string SourceUri,
        string FileName,
        string? RenderFileName,
        string ContentSha256,
        DateTimeOffset RetrievedUtc,
        string? Author,
        string? AuthorLink,
        string LicenseIdentifier,
        string LicenseUri);

    private sealed record CacheEntry(
        string CacheKey,
        DateTimeOffset RetrievedUtc,
        IReadOnlyList<string> Paths,
        long SizeBytes);
}
