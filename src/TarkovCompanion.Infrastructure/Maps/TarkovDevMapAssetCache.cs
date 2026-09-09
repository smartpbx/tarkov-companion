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
    public int MaximumCacheEntries { get; init; } = 512;

    public long MaximumCacheBytes { get; init; } = 512L * 1024 * 1024;

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

        var renderGate = _entryGates.GetOrAdd(variant.SvgPath.AbsoluteUri + "#preview", static _ => new(1, 1));
        await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryPath = result.Asset.RenderPath + ".tmp";
            try
            {
                await SvgMapRasterizer
                    .CreatePreviewAsync(result.Asset.LocalPath, temporaryPath, visibleLayer, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, result.Asset.RenderPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new(result.Asset, $"{result.Message} Showing upstream SVG layer '{visibleLayer}'.");
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            return new(null, $"The selected SVG floor is unavailable: {exception.Message}");
        }
        finally
        {
            renderGate.Release();
        }
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
                await SvgMapRasterizer
                    .CreatePreviewAsync(localPath, temporaryPreviewPath, cancellationToken)
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
        await WriteMetadataAsync(sourceUri, metadata, cancellationToken).ConfigureAwait(false);
        await EnforceCacheBoundsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
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
                    var paths = new[]
                        {
                            metadataPath,
                            Path.Combine(options.CacheDirectory, metadata.FileName),
                            metadata.RenderFileName is null
                                ? null
                                : Path.Combine(options.CacheDirectory, metadata.RenderFileName),
                        }
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
            foreach (var entry in entries
                         .Where(entry => !string.Equals(entry.CacheKey, protectedCacheKey, StringComparison.Ordinal))
                         .OrderBy(entry => entry.RetrievedUtc))
            {
                if (entryCount <= options.MaximumCacheEntries && totalBytes <= options.MaximumCacheBytes)
                {
                    break;
                }

                if (TryDeleteEntry(entry))
                {
                    entryCount--;
                    totalBytes -= entry.SizeBytes;
                }
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
