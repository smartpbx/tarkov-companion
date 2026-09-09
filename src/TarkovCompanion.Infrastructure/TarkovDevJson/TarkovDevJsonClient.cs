using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevJsonClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;
    private readonly ITarkovDevResponseCache _cache;
    private readonly DataTranslationService _translationService;
    private readonly TarkovDevJsonClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedResponse>>> _inFlight = new(StringComparer.Ordinal);

    public TarkovDevJsonClient(
        HttpClient httpClient,
        ITarkovDevResponseCache cache,
        DataTranslationService translationService,
        TarkovDevJsonClientOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _translationService = translationService;
        _options = options ?? new();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TarkovDevResponse<TarkovDevItemsData>> GetItemsAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken) =>
        GetItemsAsync(gameMode, language, false, cancellationToken);

    public Task<TarkovDevResponse<TarkovDevItemsData>> GetItemsAsync(
        GameMode gameMode,
        string language,
        bool force,
        CancellationToken cancellationToken) =>
        GetTranslatedAsync<TarkovDevItemsData>(gameMode, "items", language, force, cancellationToken);

    public Task<TarkovDevResponse<TarkovDevMapsData>> GetMapsAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken) =>
        GetMapsAsync(gameMode, language, false, cancellationToken);

    public Task<TarkovDevResponse<TarkovDevMapsData>> GetMapsAsync(
        GameMode gameMode,
        string language,
        bool force,
        CancellationToken cancellationToken) =>
        GetTranslatedAsync<TarkovDevMapsData>(gameMode, "maps", language, force, cancellationToken);

    public Task<TarkovDevResponse<TarkovDevTasksData>> GetTasksAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken) =>
        GetTasksAsync(gameMode, language, false, cancellationToken);

    public Task<TarkovDevResponse<TarkovDevTasksData>> GetTasksAsync(
        GameMode gameMode,
        string language,
        bool force,
        CancellationToken cancellationToken) =>
        GetTranslatedAsync<TarkovDevTasksData>(gameMode, "tasks", language, force, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyDictionary<string, TarkovDevHideoutStation>>> GetHideoutAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken) =>
        GetHideoutAsync(gameMode, language, false, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyDictionary<string, TarkovDevHideoutStation>>> GetHideoutAsync(
        GameMode gameMode,
        string language,
        bool force,
        CancellationToken cancellationToken) =>
        GetTranslatedAsync<IReadOnlyDictionary<string, TarkovDevHideoutStation>>(
            gameMode,
            "hideout",
            language,
            force,
            cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyDictionary<string, TarkovDevTrader>>> GetTradersAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken) =>
        GetTradersAsync(gameMode, language, false, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyDictionary<string, TarkovDevTrader>>> GetTradersAsync(
        GameMode gameMode,
        string language,
        bool force,
        CancellationToken cancellationToken) =>
        GetTranslatedAsync<IReadOnlyDictionary<string, TarkovDevTrader>>(
            gameMode,
            "traders",
            language,
            force,
            cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevCraft>>> GetCraftsAsync(
        GameMode gameMode,
        CancellationToken cancellationToken) =>
        GetCraftsAsync(gameMode, false, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevCraft>>> GetCraftsAsync(
        GameMode gameMode,
        bool force,
        CancellationToken cancellationToken) =>
        GetUntranslatedAsync<IReadOnlyList<TarkovDevCraft>>(gameMode, "crafts", force, _options.StaticFreshFor, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevBarter>>> GetBartersAsync(
        GameMode gameMode,
        CancellationToken cancellationToken) =>
        GetBartersAsync(gameMode, false, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevBarter>>> GetBartersAsync(
        GameMode gameMode,
        bool force,
        CancellationToken cancellationToken) =>
        GetUntranslatedAsync<IReadOnlyList<TarkovDevBarter>>(gameMode, "barters", force, _options.StaticFreshFor, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevPricePoint>>> GetPriceHistoryAsync(
        GameMode gameMode,
        string itemId,
        CancellationToken cancellationToken) =>
        GetPriceHistoryAsync(gameMode, itemId, false, cancellationToken);

    public Task<TarkovDevResponse<IReadOnlyList<TarkovDevPricePoint>>> GetPriceHistoryAsync(
        GameMode gameMode,
        string itemId,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return GetUntranslatedAsync<IReadOnlyList<TarkovDevPricePoint>>(
            gameMode,
            $"prices/{Uri.EscapeDataString(itemId)}",
            force,
            _options.PriceFreshFor,
            cancellationToken);
    }

    private async Task<TarkovDevResponse<T>> GetTranslatedAsync<T>(
        GameMode gameMode,
        string endpoint,
        string language,
        bool force,
        CancellationToken cancellationToken)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var path = $"{ModeSlug(gameMode)}/{endpoint}";
        var baseResponse = await GetJsonAsync(path, _options.StaticFreshFor, force, cancellationToken).ConfigureAwait(false);

        using var envelope = JsonDocument.Parse(baseResponse.Entry.BodyJson);
        var hasTranslations = envelope.RootElement.TryGetProperty("translations", out var translations) &&
            translations.ValueKind == JsonValueKind.Array &&
            translations.GetArrayLength() > 0;

        CachedResponse? translationResponse = null;
        var translatedJson = baseResponse.Entry.BodyJson;
        if (hasTranslations)
        {
            translationResponse = await GetJsonAsync(
                $"{path}_{normalizedLanguage}",
                _options.StaticFreshFor,
                force,
                cancellationToken).ConfigureAwait(false);
            translatedJson = _translationService.Apply(baseResponse.Entry.BodyJson, translationResponse.Entry.BodyJson);
        }

        var value = DeserializeEnvelope<T>(translatedJson);
        return new(
            value.Data,
            translatedJson,
            translationResponse is null || baseResponse.Entry.CachedUtc <= translationResponse.Entry.CachedUtc
                ? baseResponse.Entry.CachedUtc
                : translationResponse.Entry.CachedUtc,
            baseResponse.IsFromCache || translationResponse?.IsFromCache == true,
            baseResponse.IsStale || translationResponse?.IsStale == true,
            baseResponse.Entry.ETag,
            baseResponse.Entry.LastModified);
    }

    private async Task<TarkovDevResponse<T>> GetUntranslatedAsync<T>(
        GameMode gameMode,
        string endpoint,
        bool force,
        TimeSpan freshFor,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync(
            $"{ModeSlug(gameMode)}/{endpoint}",
            freshFor,
            force,
            cancellationToken).ConfigureAwait(false);
        var value = DeserializeEnvelope<T>(response.Entry.BodyJson);
        return new(
            value.Data,
            response.Entry.BodyJson,
            response.Entry.CachedUtc,
            response.IsFromCache,
            response.IsStale,
            response.Entry.ETag,
            response.Entry.LastModified);
    }

    private async Task<CachedResponse> GetJsonAsync(
        string cacheKey,
        TimeSpan freshFor,
        bool force,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (!force && cached is not null && now - cached.CachedUtc <= freshFor)
        {
            return new(cached, true, false);
        }

        if (!force && cached is not null)
        {
            _ = ObserveBackgroundRefreshAsync(GetOrCreateRefresh(cacheKey, cached, CancellationToken.None));
            return new(cached, true, true);
        }

        try
        {
            return await GetOrCreateRefresh(cacheKey, cached, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (cached is not null && exception is not OperationCanceledException)
        {
            return new(cached, true, true);
        }
    }

    private Task<CachedResponse> GetOrCreateRefresh(
        string cacheKey,
        TarkovDevCacheEntry? cached,
        CancellationToken cancellationToken)
    {
        var lazy = _inFlight.GetOrAdd(
            cacheKey,
            _ => new(
                () => RefreshAsync(cacheKey, cached, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndRemoveAsync(cacheKey, lazy);
    }

    private async Task<CachedResponse> AwaitAndRemoveAsync(string cacheKey, Lazy<Task<CachedResponse>> lazy)
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<CachedResponse>>>(cacheKey, lazy));
        }
    }

    private async Task<CachedResponse> RefreshAsync(
        string cacheKey,
        TarkovDevCacheEntry? cached,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = CreateRequest(cacheKey, cached);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                {
                    var revalidated = cached with { CachedUtc = _timeProvider.GetUtcNow() };
                    await _cache.PutAsync(revalidated, cancellationToken).ConfigureAwait(false);
                    return new(revalidated, true, false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = new TarkovDevRequestException(
                        $"json.tarkov.dev returned {(int)response.StatusCode} for '{cacheKey}'.",
                        response.StatusCode);
                    if (!IsTransient(response.StatusCode) || attempt == _options.MaxAttempts)
                    {
                        throw error;
                    }

                    lastError = error;
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    ValidateJsonEnvelope(body, cacheKey);
                    var entry = new TarkovDevCacheEntry(
                        cacheKey,
                        body,
                        _timeProvider.GetUtcNow(),
                        response.Headers.ETag?.ToString(),
                        response.Content.Headers.LastModified ?? response.Headers.Date);
                    await _cache.PutAsync(entry, cancellationToken).ConfigureAwait(false);
                    return new(entry, false, false);
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"json.tarkov.dev request for '{cacheKey}' timed out.", exception);
                if (attempt == _options.MaxAttempts)
                {
                    break;
                }
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
                if (attempt == _options.MaxAttempts)
                {
                    break;
                }
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        throw new TarkovDevRequestException(
            $"json.tarkov.dev request for '{cacheKey}' failed after {_options.MaxAttempts} attempts.",
            innerException: lastError);
    }

    private HttpRequestMessage CreateRequest(string cacheKey, TarkovDevCacheEntry? cached)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseAddress, cacheKey));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (cached?.ETag is not null && EntityTagHeaderValue.TryParse(cached.ETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        if (cached?.LastModified is not null)
        {
            request.Headers.IfModifiedSince = cached.LastModified;
        }

        return request;
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        if (_options.InitialRetryDelay == TimeSpan.Zero)
        {
            return;
        }

        var exponential = Math.Pow(2, attempt - 1);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        var delay = TimeSpan.FromMilliseconds(_options.InitialRetryDelay.TotalMilliseconds * exponential * jitter);
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveBackgroundRefreshAsync(Task<CachedResponse> refresh)
    {
        try
        {
            await refresh.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A valid stale response was already returned. The next caller can retry.
        }
    }

    private static TarkovDevEnvelope<T> DeserializeEnvelope<T>(string json) =>
        JsonSerializer.Deserialize<TarkovDevEnvelope<T>>(json, SerializerOptions)
        ?? throw new JsonException("json.tarkov.dev returned a null envelope.");

    private static void ValidateJsonEnvelope(string json, string cacheKey)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out _))
        {
            throw new JsonException($"json.tarkov.dev response '{cacheKey}' is missing the required data envelope.");
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static string NormalizeLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var normalized = language.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 8 || normalized.Any(character => !char.IsAsciiLetter(character) && character != '-'))
        {
            throw new ArgumentException("Language must be a BCP-47-style alphabetic code.", nameof(language));
        }

        return normalized;
    }

    private static string ModeSlug(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };

    private sealed record CachedResponse(TarkovDevCacheEntry Entry, bool IsFromCache, bool IsStale);
}
