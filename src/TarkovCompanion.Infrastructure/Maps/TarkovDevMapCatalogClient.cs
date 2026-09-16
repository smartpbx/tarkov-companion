using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

public sealed record TarkovDevMapCatalogClientOptions(
    Uri CatalogUri,
    string CacheDirectory,
    TimeSpan RefreshAfter,
    TimeSpan RequestTimeout,
    int MaximumCatalogBytes)
{
    public static TarkovDevMapCatalogClientOptions CreateDefault(string cacheDirectory) => new(
        new("https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json"),
        cacheDirectory,
        TimeSpan.FromHours(12),
        TimeSpan.FromSeconds(15),
        4 * 1024 * 1024);
}

public sealed class TarkovDevMapCatalogClient(
    HttpClient httpClient,
    TarkovDevMapCatalogClientOptions options,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MapCatalogLoadResult> GetAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogCacheDocument? cached = null;
            string? cacheError = null;
            try
            {
                cached = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                cacheError = exception.Message;
            }

            if (cached is not null)
            {
                try
                {
                    _ = ParseCached(cached, MapCatalogAvailability.Cached, "Validating local catalog cache.");
                }
                catch (InvalidDataException exception)
                {
                    cacheError = exception.Message;
                    cached = null;
                }
            }

            var now = _timeProvider.GetUtcNow();
            if (cached is not null && now - cached.RetrievedUtc <= options.RefreshAfter)
            {
                return ParseCached(cached, MapCatalogAvailability.Cached, "Using the current local catalog cache.");
            }

            try
            {
                return await RefreshAsync(cached, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                return cached is not null
                    ? ParseCached(cached, MapCatalogAvailability.OfflineCached, $"Catalog refresh failed; using offline cache: {exception.Message}")
                    : new(
                        null,
                        MapCatalogAvailability.Unavailable,
                        cacheError is null
                            ? $"Map catalog is unavailable: {exception.Message}"
                            : $"Map catalog and local cache are unavailable: {exception.Message}; cache: {cacheError}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MapCatalogLoadResult> RefreshAsync(
        CatalogCacheDocument? cached,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.CatalogUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TarkovCompanion", "1.0"));
        if (!string.IsNullOrWhiteSpace(cached?.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        if (cached?.LastModifiedUtc is { } lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            var revalidated = cached with { RetrievedUtc = now };
            await WriteCacheAsync(revalidated, cancellationToken).ConfigureAwait(false);
            return ParseCached(revalidated, MapCatalogAvailability.Current, "Catalog cache revalidated with tarkov.dev.");
        }

        response.EnsureSuccessStatusCode();
        var json = await ReadBoundedStringAsync(response.Content, options.MaximumCatalogBytes, timeout.Token).ConfigureAwait(false);
        var catalog = TarkovDevMapCatalogParser.Parse(json, options.CatalogUri, now);
        var cacheDocument = new CatalogCacheDocument(
            options.CatalogUri.AbsoluteUri,
            now,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            catalog.Provenance.ContentSha256,
            json);
        await WriteCacheAsync(cacheDocument, cancellationToken).ConfigureAwait(false);
        return new(catalog, MapCatalogAvailability.Current, "Catalog refreshed from tarkov.dev.", json);
    }

    private MapCatalogLoadResult ParseCached(
        CatalogCacheDocument cached,
        MapCatalogAvailability availability,
        string message)
    {
        var catalog = TarkovDevMapCatalogParser.Parse(cached.Json, options.CatalogUri, cached.RetrievedUtc, availability);
        if (!string.Equals(catalog.Provenance.ContentSha256, cached.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The cached map catalog failed its content hash check.");
        }

        return new(catalog, availability, message, cached.Json);
    }

    private async Task<CatalogCacheDocument?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath();
        if (!File.Exists(cachePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(cachePath);
        if (fileInfo.Length > options.MaximumCatalogBytes * 2L)
        {
            throw new InvalidDataException("The local map catalog cache exceeds its size limit.");
        }

        await using var stream = new FileStream(
            cachePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var cached = await JsonSerializer
            .DeserializeAsync<CatalogCacheDocument>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (cached is null ||
            !string.Equals(cached.SourceUri, options.CatalogUri.AbsoluteUri, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(cached.Json) ||
            string.IsNullOrWhiteSpace(cached.ContentSha256))
        {
            throw new InvalidDataException("The local map catalog cache metadata is invalid.");
        }

        return cached;
    }

    private async Task WriteCacheAsync(CatalogCacheDocument cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        var cachePath = GetCachePath();
        var temporaryPath = cachePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, cachePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetCachePath() => Path.Combine(options.CacheDirectory, "tarkov-dev-maps.cache.json");

    private void ValidateOptions()
    {
        if (!options.CatalogUri.IsAbsoluteUri || options.CatalogUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The tarkov.dev catalog URI must be an absolute HTTPS URI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        if (options.RefreshAfter < TimeSpan.Zero || options.RequestTimeout <= TimeSpan.Zero || options.MaximumCatalogBytes <= 0)
        {
            throw new InvalidOperationException("The tarkov.dev catalog client bounds must be positive.");
        }
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The tarkov.dev map catalog exceeds its size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The tarkov.dev map catalog exceeds its size limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        memory.Position = 0;
        using var reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRecoverable(Exception exception, CancellationToken callerToken) => exception switch
    {
        OperationCanceledException when callerToken.IsCancellationRequested => false,
        HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException => true,
        _ => false,
    };

    private sealed record CatalogCacheDocument(
        string SourceUri,
        DateTimeOffset RetrievedUtc,
        string? ETag,
        DateTimeOffset? LastModifiedUtc,
        string ContentSha256,
        string Json);
}
