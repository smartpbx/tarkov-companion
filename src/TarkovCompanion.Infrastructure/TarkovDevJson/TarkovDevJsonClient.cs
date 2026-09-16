using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevJsonClient : IAsyncDisposable
{
    private const int MaximumTranslationPathUtf8Bytes = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        // Per-client envelope validation enforces the configured limit first. Keep the typed
        // serializer at the largest accepted option so a valid 33-64-depth configuration is not
        // silently tightened during the second parse.
        MaxDepth = 64,
    };

    private readonly HttpClient _httpClient;
    private readonly ITarkovDevResponseCache _cache;
    private readonly DataTranslationService _translationService;
    private readonly TarkovDevJsonClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _afterForegroundRefreshRegistered;
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedResponse>>> _foregroundRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedResponse>>> _forcedRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _backgroundRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _publicationEpochs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _backgroundGate = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private int _disposeState;
    private Task? _disposeTask;

    public TarkovDevJsonClient(
        HttpClient httpClient,
        ITarkovDevResponseCache cache,
        DataTranslationService translationService,
        TarkovDevJsonClientOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, cache, translationService, options, timeProvider, null)
    {
    }

    internal TarkovDevJsonClient(
        HttpClient httpClient,
        ITarkovDevResponseCache cache,
        DataTranslationService translationService,
        TarkovDevJsonClientOptions? options,
        TimeProvider? timeProvider,
        Action<string>? afterForegroundRefreshRegistered)
    {
        _httpClient = httpClient;
        _cache = cache;
        _translationService = translationService;
        _options = options ?? new();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _afterForegroundRefreshRegistered = afterForegroundRefreshRegistered;
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
        var baseResponse = await GetJsonAsync(
            path,
            _options.StaticFreshFor,
            force,
            (candidate, previous) => ValidateTranslatableDataset<T>(candidate, previous, path),
            cancellationToken).ConfigureAwait(false);

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
                (candidate, previous) =>
                {
                    ValidateTranslationEnvelope(candidate, path);
                    var translatedCandidate = _translationService.Apply(
                        baseResponse.Entry.BodyJson,
                        candidate,
                        _options.MaximumResponseBytes);
                    var translatedPrevious = previous is null
                        ? null
                        : _translationService.Apply(
                            baseResponse.Entry.BodyJson,
                            previous,
                            _options.MaximumResponseBytes);
                    ValidateTypedDataset<T>(translatedCandidate, translatedPrevious, path);
                },
                cancellationToken).ConfigureAwait(false);
            try
            {
                translatedJson = _translationService.Apply(
                    baseResponse.Entry.BodyJson,
                    translationResponse.Entry.BodyJson,
                    _options.MaximumResponseBytes);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // A coalesced translation transfer may have been validated by a concurrent
                // caller against a different base generation. Re-check this exact pair at the
                // return boundary and keep an incompatible pair out of normalized persistence.
                throw Refused(path, exception);
            }
        }

        // Translation candidates are merged and typed before publication above. Repeating the
        // typed read here keeps the returned value and the validated value identical without a
        // second quarantine path that could ever run without an exact cache hash.
        TarkovDevEnvelope<T> value;
        try
        {
            value = DeserializeEnvelope<T>(translatedJson);
            TarkovDevDatasetValidator.Validate(value.Data, path);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw Refused(path, exception);
        }

        return new(
            value.Data,
            translatedJson,
            translationResponse is null || baseResponse.Entry.CachedUtc <= translationResponse.Entry.CachedUtc
                ? baseResponse.Entry.CachedUtc
                : translationResponse.Entry.CachedUtc,
            baseResponse.IsFromCache || translationResponse?.IsFromCache == true,
            baseResponse.IsStale || translationResponse?.IsStale == true,
            baseResponse.Entry.ETag,
            baseResponse.Entry.LastModified,
            baseResponse.Entry.BodyJson,
            translationResponse?.RefusalReason ?? baseResponse.RefusalReason,
            path);
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
            (candidate, previous) => ValidateTypedDataset<T>(candidate, previous, endpoint),
            cancellationToken).ConfigureAwait(false);
        var value = DeserializeEnvelope<T>(response.Entry.BodyJson);
        return new(
            value.Data,
            response.Entry.BodyJson,
            response.Entry.CachedUtc,
            response.IsFromCache,
            response.IsStale,
            response.Entry.ETag,
            response.Entry.LastModified,
            response.Entry.BodyJson,
            response.RefusalReason,
            $"{ModeSlug(gameMode)}/{endpoint}");
    }

    private async Task<CachedResponse> GetJsonAsync(
        string cacheKey,
        TimeSpan freshFor,
        bool force,
        Action<string, string?> validate,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var cached = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            try
            {
                // Cache rows predate validators and survive application upgrades. Re-check the
                // complete typed persistence shape before a fresh shortcut can make an old,
                // structurally-valid but unusable document authoritative forever.
                ValidateCachedResponsePolicy(cached.BodyJson, cacheKey);
                validate(cached.BodyJson, null);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or TarkovDevResponseBudgetException)
            {
                await _cache.QuarantineAsync(
                    cacheKey,
                    cached.ContentSha256 ?? ContentHash(cached.BodyJson),
                    "cache-dataset-invalid",
                    cancellationToken).ConfigureAwait(false);
                cached = null;
            }
        }

        var now = _timeProvider.GetUtcNow();
        if (!force && cached is not null && now - cached.CachedUtc <= freshFor)
        {
            return new(cached, true, false);
        }

        if (!force && cached is not null)
        {
            StartBackgroundRefresh(cacheKey, cached, validate);
            return new(cached, true, true);
        }

        try
        {
            // Forced callers share a transfer that is independent from stale background work.
            // Every transfer belongs to the client lifetime while each waiter retains independent
            // cancellation, so disposal can always cancel and drain the actual HTTP operation.
            return force
                ? await GetOrCreateForcedRefreshAsync(cacheKey, cached, validate, cancellationToken).ConfigureAwait(false)
                : await GetOrCreateForegroundRefreshAsync(cacheKey, validate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (cached is not null && exception is not OperationCanceledException)
        {
            return new(
                cached,
                true,
                true,
                DatasetRefusalReason(exception));
        }
    }

    private Task<CachedResponse> GetOrCreateForegroundRefreshAsync(
        string cacheKey,
        Action<string, string?> validate,
        CancellationToken callerCancellation)
    {
        Lazy<Task<CachedResponse>> candidate;
        Lazy<Task<CachedResponse>> selected;
        lock (_backgroundGate)
        {
            if (_disposeState != 0)
            {
                throw new ObjectDisposedException(nameof(TarkovDevJsonClient));
            }

            // A cache-miss reader arriving while a forced refresh owns this key must join the
            // authoritative transfer. Starting a second foreground request here would capture the
            // force epoch and could publish a different body in the small window between the
            // forced write and its epoch retirement.
            if (_forcedRefreshes.TryGetValue(cacheKey, out var forced))
            {
                return forced.Value.WaitAsync(callerCancellation);
            }

            // Capture while registration is serialized. If the Lazy captured on first execution,
            // a force could register and increment after this normal transfer was registered but
            // before Lazy.Value started, making the older transfer look current.
            var publicationEpoch = CapturePublicationEpoch(cacheKey);
            candidate = new(
                () => RefreshAsync(
                    cacheKey,
                    null,
                    validate,
                    publicationEpoch,
                    _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
            selected = _foregroundRefreshes.GetOrAdd(cacheKey, candidate);
        }

        if (ReferenceEquals(candidate, selected))
        {
            // The integration seam pauses only after both registration and epoch capture. It
            // makes the once-sub-instruction race reproducible without weakening production
            // ordering; null in every production composition.
            _afterForegroundRefreshRegistered?.Invoke(cacheKey);
            _ = AwaitForegroundAndRemoveAsync(cacheKey, candidate);
        }

        // The transfer is token-independent so one caller cannot cancel another caller's shared
        // first load. Each waiter still observes its own cancellation immediately; client disposal
        // cancels and drains the shared transfer itself.
        return selected.Value.WaitAsync(callerCancellation);
    }

    private Task<CachedResponse> GetOrCreateForcedRefreshAsync(
        string cacheKey,
        TarkovDevCacheEntry? cached,
        Action<string, string?> validate,
        CancellationToken callerCancellation)
    {
        Lazy<Task<CachedResponse>> candidate;
        Lazy<Task<CachedResponse>> selected;
        lock (_backgroundGate)
        {
            if (_disposeState != 0)
            {
                throw new ObjectDisposedException(nameof(TarkovDevJsonClient));
            }

            candidate = new(
                () => RunForcedRefreshAsync(cacheKey, cached, validate, _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
            selected = _forcedRefreshes.GetOrAdd(cacheKey, candidate);
        }

        if (ReferenceEquals(candidate, selected))
        {
            _ = AwaitForcedAndRemoveAsync(cacheKey, candidate);
        }

        return selected.Value.WaitAsync(callerCancellation);
    }

    private async Task<CachedResponse> RunForcedRefreshAsync(
        string cacheKey,
        TarkovDevCacheEntry? cached,
        Action<string, string?> validate,
        CancellationToken cancellationToken)
    {
        var epoch = await BeginForcedPublicationAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshAsync(cacheKey, cached, validate, epoch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Invalidate work that began while this authoritative refresh was active. The forced
            // body has already published (when successful); a delayed stale response must not be
            // able to replace it after the caller has observed completion.
            await EndForcedPublicationAsync(cacheKey, epoch).ConfigureAwait(false);
        }
    }

    private async Task AwaitForcedAndRemoveAsync(string cacheKey, Lazy<Task<CachedResponse>> lazy)
    {
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Waiters observe their own result. This observer only owns exact-entry retirement.
        }
        finally
        {
            lock (_backgroundGate)
            {
                _forcedRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task<CachedResponse>>>(cacheKey, lazy));
                RetirePublicationEpochIfIdle(cacheKey);
            }
        }
    }

    private async Task AwaitForegroundAndRemoveAsync(string cacheKey, Lazy<Task<CachedResponse>> lazy)
    {
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The requesting callers observe the failure. This observer exists only to retire the
            // exact lazy entry without turning its duplicate-suppression task into an unobserved one.
        }
        finally
        {
            lock (_backgroundGate)
            {
                _foregroundRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task<CachedResponse>>>(cacheKey, lazy));
                RetirePublicationEpochIfIdle(cacheKey);
            }
        }
    }

    private void StartBackgroundRefresh(
        string cacheKey,
        TarkovDevCacheEntry cached,
        Action<string, string?> validate)
    {
        Lazy<Task> candidate;
        Lazy<Task> selected;
        lock (_backgroundGate)
        {
            if (_disposeState != 0)
            {
                return;
            }

            // A forced request is already replacing this stale entry. Suppressing a redundant
            // background transfer is what gives the force epoch a single publisher; otherwise a
            // background request begun during the force could share its epoch and win afterward.
            if (_forcedRefreshes.ContainsKey(cacheKey))
            {
                return;
            }

            // See the foreground path: registration and epoch capture are one ordering event.
            var publicationEpoch = CapturePublicationEpoch(cacheKey);
            candidate = new(
                () => ObserveBackgroundRefreshAsync(
                    cacheKey,
                    cached,
                    validate,
                    publicationEpoch,
                    _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
            selected = _backgroundRefreshes.GetOrAdd(cacheKey, candidate);
        }

        if (ReferenceEquals(candidate, selected))
        {
            _ = AwaitBackgroundAndRemoveAsync(cacheKey, candidate);
        }
    }

    private async Task AwaitBackgroundAndRemoveAsync(string cacheKey, Lazy<Task> lazy)
    {
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            lock (_backgroundGate)
            {
                _backgroundRefreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(cacheKey, lazy));
                RetirePublicationEpochIfIdle(cacheKey);
            }
        }
    }

    /// <summary>Retires completed per-key ordering state without creating an epoch ABA race.</summary>
    /// <remarks>
    /// Every refresh registers its lazy under <see cref="_backgroundGate"/> before capturing an
    /// epoch. Removing the epoch under that same gate is therefore safe only after all three
    /// refresh registries are empty: a newly registered transfer keeps the epoch, and a delayed
    /// old transfer keeps it until its publication check and observer have both completed.
    /// </remarks>
    private void RetirePublicationEpochIfIdle(string cacheKey)
    {
        if (!_foregroundRefreshes.ContainsKey(cacheKey) &&
            !_forcedRefreshes.ContainsKey(cacheKey) &&
            !_backgroundRefreshes.ContainsKey(cacheKey))
        {
            _publicationEpochs.TryRemove(cacheKey, out _);
        }
    }

    private async Task<CachedResponse> RefreshAsync(
        string cacheKey,
        TarkovDevCacheEntry? cached,
        Action<string, string?> validate,
        long publicationEpoch,
        CancellationToken cancellationToken)
    {
        if (_options.OfflineProbe())
        {
            throw new TarkovDevOfflineException();
        }

        Exception? lastError = null;
        // The mirror first where there is one, then upstream, always. A group server holding
        // the catalog saves every client from pulling the same several megabytes, and a mirror
        // that is off, unreachable or answering badly costs nothing: the loop moves on to the
        // address the client used before a mirror existed.
        foreach (var address in Addresses())
        {
            var isUpstream = address == _options.BaseAddress;
            var result = await TryAsync(
                address,
                // One go at a mirror. Retrying something optional while upstream is sitting
                // there waiting is time the player spends looking at an empty database.
                isUpstream ? _options.MaxAttempts : 1,
                isUpstream,
                cacheKey,
                cached,
                validate,
                publicationEpoch,
                cancellationToken).ConfigureAwait(false);
            if (result.Response is { } response)
            {
                return response;
            }

            lastError = result.Error ?? lastError;
        }

        throw new TarkovDevRequestException(
            $"The catalog request for '{cacheKey}' failed against every address.",
            innerException: lastError);
    }

    /// <summary>One address, tried as many times as it is worth trying.</summary>
    /// <remarks>
    /// A failure against a mirror is returned rather than thrown, because upstream is next and
    /// it is the answer the client would have had anyway. A failure against upstream is thrown
    /// where the status says retrying cannot help, because there is nothing after it.
    /// </remarks>
    private async Task<(CachedResponse? Response, Exception? Error)> TryAsync(
        Uri address,
        int attempts,
        bool isUpstream,
        string cacheKey,
        TarkovDevCacheEntry? cached,
        Action<string, string?> validate,
        long publicationEpoch,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_options.OfflineProbe())
                {
                    throw new TarkovDevOfflineException();
                }

                using var request = CreateRequest(address, cacheKey, cached);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                {
                    var revalidated = cached with { CachedUtc = _timeProvider.GetUtcNow() };
                    if (!await PublishIfCurrentAsync(
                            cacheKey,
                            publicationEpoch,
                            revalidated,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return (
                            await ResolveSupersededResponseAsync(cacheKey, cancellationToken)
                                .ConfigureAwait(false),
                            null);
                    }

                    return (new(revalidated, true, false), null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = new TarkovDevRequestException(
                        $"The catalog at {address} returned {(int)response.StatusCode} for '{cacheKey}'.",
                        response.StatusCode);
                    if (isUpstream && (!IsTransient(response.StatusCode) || attempt == attempts))
                    {
                        throw error;
                    }

                    lastError = error;
                    if (!isUpstream)
                    {
                        return (null, error);
                    }
                }
                else
                {
                    var body = await ReadBoundedUtf8Async(response.Content, timeout.Token).ConfigureAwait(false);
                    ValidateJsonEnvelope(body, cacheKey);
                    try
                    {
                        validate(body, cached?.BodyJson);
                    }
                    catch (Exception exception) when (
                        (exception is JsonException or InvalidDataException) &&
                        DatasetRefusalReason(exception) is null)
                    {
                        throw Refused(cacheKey, exception);
                    }
                    var entry = new TarkovDevCacheEntry(
                        cacheKey,
                        body,
                        _timeProvider.GetUtcNow(),
                        response.Headers.ETag?.ToString(),
                        response.Content.Headers.LastModified ?? response.Headers.Date,
                        ContentHash(body));
                    if (!await PublishIfCurrentAsync(
                            cacheKey,
                            publicationEpoch,
                            entry,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return (
                            await ResolveSupersededResponseAsync(cacheKey, cancellationToken)
                                .ConfigureAwait(false),
                            null);
                    }

                    return (new(entry, false, false), null);
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"The catalog request for '{cacheKey}' timed out.", exception);
                if (attempt == attempts)
                {
                    break;
                }
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
                if (attempt == attempts)
                {
                    break;
                }
            }
            catch (InvalidDataException exception)
            {
                // InvalidDataException derives from IOException, so hostile-dataset refusal must
                // be classified before the transport-stream catch below. Otherwise malformed
                // normalized input is retried and ultimately reported as a generic outage.
                if (isUpstream)
                {
                    if (DatasetRefusalReason(exception) is not null)
                    {
                        throw;
                    }

                    throw Refused(cacheKey, exception);
                }

                lastError = exception;
                break;
            }
            catch (IOException exception)
            {
                // A response stream can reset after headers have arrived. HttpClient does not
                // consistently wrap that transport failure in HttpRequestException.
                lastError = exception;
                if (attempt == attempts)
                {
                    break;
                }
            }
            catch (TarkovDevOfflineException)
            {
                throw;
            }
            catch (TarkovDevResponseBudgetException exception)
            {
                if (isUpstream)
                {
                    throw Refused(cacheKey, exception);
                }

                lastError = exception;
                break;
            }
            catch (DecoderFallbackException exception)
            {
                if (isUpstream)
                {
                    throw Refused(cacheKey, exception);
                }

                lastError = exception;
                break;
            }
            catch (JsonException exception)
            {
                // A mirror serving something that is not the catalog is a mirror to walk away
                // from, not one to retry. Upstream is next.
                if (isUpstream)
                {
                    throw Refused(cacheKey, exception);
                }

                lastError = exception;
                break;
            }
            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        return (null, lastError);
    }

    /// <summary>
    /// Where to ask, in order: the group's mirror where there is one, then upstream.
    /// </summary>
    /// <remarks>
    /// Upstream is always in the list and always last. A mirror is an optimisation, and the
    /// moment it can stop a client working it has stopped being one.
    /// </remarks>
    private IEnumerable<Uri> Addresses()
    {
        if (_options.MirrorAddress is { } mirror)
        {
            yield return mirror;
        }

        yield return _options.BaseAddress;
    }

    private HttpRequestMessage CreateRequest(Uri address, string cacheKey, TarkovDevCacheEntry? cached)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, cacheKey));
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

    private async Task ObserveBackgroundRefreshAsync(
        string cacheKey,
        TarkovDevCacheEntry cached,
        Action<string, string?> validate,
        long publicationEpoch,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _options.MaximumOfflineReconnectAttempts; attempt++)
        {
            try
            {
                await RefreshAsync(cacheKey, cached, validate, publicationEpoch, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TarkovDevOfflineException) when (attempt < _options.MaximumOfflineReconnectAttempts)
            {
                if (_options.OfflineReconnectDelay > TimeSpan.Zero)
                {
                    var multiplier = Math.Min(1 << Math.Min(attempt, 5), 32);
                    await Task.Delay(_options.OfflineReconnectDelay * multiplier, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A valid stale response was already returned. The next caller can retry.
                return;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_backgroundGate)
        {
            if (_disposeTask is not null)
            {
                return new(_disposeTask);
            }

            _disposeState = 1;
            _lifetime.Cancel();
            var pending = _backgroundRefreshes.Values.Select(refresh => refresh.Value)
                .Concat(_foregroundRefreshes.Values.Select(refresh => (Task)refresh.Value))
                .Concat(_forcedRefreshes.Values.Select(refresh => (Task)refresh.Value))
                .ToArray();
            _disposeTask = DrainBackgroundAsync(pending);
            return new(_disposeTask);
        }
    }

    private async Task DrainBackgroundAsync(Task[] pending)
    {
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Request callers have already observed foreground failures, and background refreshes
            // are best-effort. Shutdown's job is to quiesce them, not replace the initiating result.
        }
        finally
        {
            _publicationGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(nameof(TarkovDevJsonClient));
        }
    }

    private static TarkovDevEnvelope<T> DeserializeEnvelope<T>(string json) =>
        JsonSerializer.Deserialize<TarkovDevEnvelope<T>>(json, SerializerOptions)
        ?? throw new JsonException("json.tarkov.dev returned a null envelope.");

    private static string ContentHash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private void ValidateJsonEnvelope(string json, string cacheKey)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = _options.MaximumJsonDepth });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out _))
        {
            throw new JsonException($"json.tarkov.dev response '{cacheKey}' is missing the required data envelope.");
        }
    }

    private void ValidateCachedResponsePolicy(string json, string cacheKey)
    {
        if (Encoding.UTF8.GetByteCount(json) > _options.MaximumResponseBytes)
        {
            throw new TarkovDevResponseBudgetException(_options.MaximumResponseBytes);
        }

        // Deserialization has a process-wide ceiling of 64 so every network candidate is first
        // parsed with this client instance's (possibly tighter) policy. Cached bodies need the
        // same check or a cache written under looser settings bypasses today's depth limit.
        ValidateJsonEnvelope(json, cacheKey);
    }

    private void ValidateTranslationEnvelope(string json, string cacheKey)
    {
        ValidateJsonEnvelope(json, cacheKey);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = _options.MaximumJsonDepth });
        if (document.RootElement.GetProperty("data").ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException($"json.tarkov.dev translation '{cacheKey}' must contain one data object.");
        }
    }

    private void ValidateTranslatableDataset<T>(string json, string? previousJson, string cacheKey)
    {
        ValidateTypedDataset<T>(json, previousJson, cacheKey);

        // Parsing translation directives only after the base response is cached turns an
        // unsupported path into a durable poison entry: every retry fails while the fresh-cache
        // shortcut keeps returning the same base. The translation service's path grammar has one
        // required prefix; check it without cloning and serializing a multi-megabyte data tree.
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = _options.MaximumJsonDepth });
        if (!document.RootElement.TryGetProperty("translations", out var translations))
        {
            return;
        }

        if (translations.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException($"json.tarkov.dev response '{cacheKey}' has malformed translation directives.");
        }

        foreach (var element in translations.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.String)
            {
                throw new JsonException($"json.tarkov.dev response '{cacheKey}' has a non-string translation directive.");
            }

            var path = element.GetString();
            if (path is not null && Encoding.UTF8.GetByteCount(path) > MaximumTranslationPathUtf8Bytes)
            {
                throw new JsonException(
                    $"json.tarkov.dev response '{cacheKey}' contains an oversized translation directive.");
            }

            if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith("$.", StringComparison.Ordinal))
            {
                throw new JsonException($"Unsupported translation path '{path}'.");
            }
        }
    }

    private static void ValidateTypedDataset<T>(string json, string? previousJson, string cacheKey)
    {
        var candidate = DeserializeEnvelope<T>(json);
        TarkovDevDatasetValidator.Validate(candidate.Data, cacheKey);
        var incoming = DatasetCardinalities(candidate.Data);
        if (incoming[0].Count == 0)
        {
            throw new JsonException($"json.tarkov.dev response '{cacheKey}' contains an empty dataset.");
        }

        if (previousJson is null)
        {
            return;
        }

        var previousData = DeserializeEnvelope<T>(previousJson).Data;
        ValidateStableNestedCardinalities(candidate.Data, previousData, cacheKey);
        var previous = DatasetCardinalities(previousData)
            .ToDictionary(value => value.Name, value => value.Count, StringComparer.Ordinal);
        foreach (var dimension in incoming)
        {
            if (previous.TryGetValue(dimension.Name, out var held) &&
                held > 0 &&
                dimension.Count * 2 < held)
            {
                throw new JsonException(
                    $"json.tarkov.dev response '{cacheKey}' implausibly shrank {dimension.Name} " +
                    $"from {held:N0} to {dimension.Count:N0} records.");
            }
        }
    }

    private static void ValidateStableNestedCardinalities<T>(T candidate, T previous, string cacheKey)
    {
        if (candidate is not TarkovDevMapsData candidateMaps || previous is not TarkovDevMapsData previousMaps)
        {
            return;
        }

        foreach (var heldPair in previousMaps.Maps)
        {
            if (!candidateMaps.Maps.TryGetValue(heldPair.Key, out var incomingMap))
            {
                // Maps can legitimately leave rotation. The aggregate guard still rejects a
                // catalog-wide collapse; per-map guards apply only to a map the source retained.
                continue;
            }

            RefuseNestedShrink(
                cacheKey,
                $"map extracts for map '{heldPair.Key}'",
                heldPair.Value.Extracts.Count,
                incomingMap.Extracts.Count);
            RefuseNestedShrink(
                cacheKey,
                $"map locks for map '{heldPair.Key}'",
                heldPair.Value.Locks.Count,
                incomingMap.Locks.Count);
            RefuseNestedShrink(
                cacheKey,
                $"loot-container positions for map '{heldPair.Key}'",
                heldPair.Value.LootContainers.Count,
                incomingMap.LootContainers.Count);
            RefuseNestedShrink(
                cacheKey,
                $"loose-loot positions for map '{heldPair.Key}'",
                heldPair.Value.LootLoose.Count,
                incomingMap.LootLoose.Count);
            RefuseNestedShrink(
                cacheKey,
                $"loose-loot candidates for map '{heldPair.Key}'",
                heldPair.Value.LootLoose.Sum(position => (long)position.Items.Count),
                incomingMap.LootLoose.Sum(position => (long)position.Items.Count));
        }
    }

    private static void RefuseNestedShrink(
        string cacheKey,
        string dimension,
        long held,
        long incoming)
    {
        if (held > 0 && incoming * 2 < held)
        {
            throw new JsonException(
                $"json.tarkov.dev response '{cacheKey}' implausibly shrank {dimension} " +
                $"from {held:N0} to {incoming:N0} records.");
        }
    }

    private static IReadOnlyList<DatasetCardinality> DatasetCardinalities<T>(T data) => data switch
    {
        TarkovDevItemsData items =>
        [
            new("items", items.Items.Count),
            new("item categories", items.ItemCategories.Count),
            new("item category memberships", items.Items.Values.Sum(item => (long)item.Categories.Count)),
            new("item trader offers", items.Items.Values.Sum(item => (long)item.SellToTrader.Count)),
        ],
        TarkovDevMapsData maps =>
        [
            new("maps", maps.Maps.Count),
            new("map extracts", maps.Maps.Values.Sum(map => (long)map.Extracts.Count)),
            new("map locks", maps.Maps.Values.Sum(map => (long)map.Locks.Count)),
            new("map loot containers", maps.Maps.Values.Sum(map => (long)map.LootContainers.Count)),
            new("map loose-loot positions", maps.Maps.Values.Sum(map => (long)map.LootLoose.Count)),
            new("map loose-loot candidates", maps.Maps.Values.Sum(map =>
                map.LootLoose.Sum(position => (long)position.Items.Count))),
        ],
        TarkovDevTasksData tasks =>
        [
            new("tasks", tasks.Tasks.Count),
            new("task prerequisites", tasks.Tasks.Values.Sum(task => (long)task.TaskRequirements.Count)),
            new("task prerequisite statuses", tasks.Tasks.Values.Sum(task =>
                task.TaskRequirements.Sum(requirement => (long)requirement.Status.Count))),
            new("task objectives", tasks.Tasks.Values.Sum(task => (long)task.Objectives.Count)),
            new("task failure conditions", tasks.Tasks.Values.Sum(task => (long)task.FailConditions.Count)),
            new("objective target statuses", TaskObjectives(tasks).Sum(objective => (long)objective.Status.Count)),
            new("objective item targets", TaskObjectives(tasks).Sum(ObjectiveItemTargetCount)),
            new("objective map links", TaskObjectives(tasks).Sum(ObjectiveMapLinkCount)),
            new("objective zones", TaskObjectives(tasks).Sum(ObjectiveZoneCount)),
        ],
        IReadOnlyDictionary<string, TarkovDevHideoutStation> hideout =>
        [
            new("hideout stations", hideout.Count),
            new("hideout levels", hideout.Values.Sum(station => (long)station.Levels.Count)),
            new("hideout item requirements", HideoutLevels(hideout).Sum(level => (long)level.ItemRequirements.Count)),
            new("hideout station requirements", HideoutLevels(hideout).Sum(level => (long)level.StationLevelRequirements.Count)),
            new("hideout trader requirements", HideoutLevels(hideout).Sum(level => (long)level.TraderRequirements.Count)),
            new("hideout skill requirements", HideoutLevels(hideout).Sum(level => (long)level.SkillRequirements.Count)),
        ],
        IReadOnlyDictionary<string, TarkovDevTrader> traders =>
        [
            new("traders", traders.Count),
            new("trader levels", traders.Values.Sum(trader => (long)trader.Levels.Count)),
        ],
        IReadOnlyCollection<TarkovDevCraft> crafts =>
        [
            new("crafts", crafts.Count),
            new("craft requirements", crafts.Sum(craft => (long)craft.RequiredItems.Count)),
        ],
        IReadOnlyCollection<TarkovDevBarter> barters =>
        [
            new("barters", barters.Count),
            new("barter requirements", barters.Sum(barter => (long)barter.RequiredItems.Count)),
        ],
        IReadOnlyCollection<TarkovDevPricePoint> prices => [new("prices", prices.Count)],
        _ => throw new JsonException($"No hostile-input cardinality rule is registered for {typeof(T).Name}.")
    };

    private static IEnumerable<TarkovDevTaskObjective> TaskObjectives(TarkovDevTasksData tasks) =>
        tasks.Tasks.Values.SelectMany(task => task.Objectives.Concat(task.FailConditions));

    private static long ObjectiveItemTargetCount(TarkovDevTaskObjective objective) =>
        objective.Items.Count +
        objective.UseAny.Count +
        objective.RequiredKeys.Sum(group => (long)group.Count) +
        (string.IsNullOrWhiteSpace(objective.Item) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(objective.QuestItem) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(objective.MarkerItem) ? 0 : 1);

    private static long ObjectiveMapLinkCount(TarkovDevTaskObjective objective) =>
        objective.Maps.Count +
        objective.Zones.LongCount(zone => !string.IsNullOrWhiteSpace(zone.Map)) +
        objective.PossibleLocations.LongCount(location => !string.IsNullOrWhiteSpace(location.Map));

    private static long ObjectiveZoneCount(TarkovDevTaskObjective objective) =>
        objective.Zones.Count +
        objective.PossibleLocations.Sum(location => (long)location.Positions.Count);

    private static IEnumerable<TarkovDevHideoutLevel> HideoutLevels(
        IReadOnlyDictionary<string, TarkovDevHideoutStation> hideout) =>
        hideout.Values.SelectMany(station => station.Levels);

    private static InvalidDataException Refused(string cacheKey, Exception exception)
    {
        var reason = $"Refused '{cacheKey}': {exception.Message}";
        return new(reason, new TarkovDevDatasetRefusalMarker(reason, exception));
    }

    private static string? DatasetRefusalReason(Exception exception) =>
        exception is InvalidDataException
        {
            InnerException: TarkovDevDatasetRefusalMarker refusal,
        }
            ? refusal.Reason
            : null;

    private long CapturePublicationEpoch(string cacheKey) =>
        _publicationEpochs.GetOrAdd(cacheKey, 0);

    private async Task<long> BeginForcedPublicationAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _publicationEpochs.AddOrUpdate(
                cacheKey,
                1,
                static (_, current) => checked(current + 1));
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private async Task EndForcedPublicationAsync(string cacheKey, long forcedEpoch)
    {
        await _publicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_publicationEpochs.TryGetValue(cacheKey, out var current) && current == forcedEpoch)
            {
                _publicationEpochs[cacheKey] = checked(current + 1);
            }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private async Task<bool> PublishIfCurrentAsync(
        string cacheKey,
        long publicationEpoch,
        TarkovDevCacheEntry entry,
        CancellationToken cancellationToken)
    {
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CapturePublicationEpoch(cacheKey) == publicationEpoch)
            {
                await _cache.PutAsync(entry, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private async Task<CachedResponse> ResolveSupersededResponseAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        // Losing the publication epoch means a force refresh became authoritative after this
        // transfer started. Returning this transfer's perfectly valid but obsolete body would
        // let its caller replace the normalized tables even though the raw-cache fence held.
        // Join the authoritative transfer while it is still registered; after retirement its
        // successful body must be the cache entry. If neither exists, the force failed and the
        // superseded body is deliberately refused rather than allowed to escape the fence.
        if (_forcedRefreshes.TryGetValue(cacheKey, out var forced))
        {
            return await forced.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var authoritative = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (authoritative is not null)
        {
            return new(authoritative, true, false);
        }

        throw new TarkovDevRequestException(
            $"The catalog response for '{cacheKey}' was superseded, but its authoritative replacement did not complete.");
    }

    private async Task<string> ReadBoundedUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > _options.MaximumResponseBytes)
        {
            throw new TarkovDevResponseBudgetException(_options.MaximumResponseBytes);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > _options.MaximumResponseBytes)
                {
                    throw new TarkovDevResponseBudgetException(_options.MaximumResponseBytes);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            // MemoryStream owns an exposable buffer here. Decoding that buffer avoids a second
            // response-sized allocation at the configured limit (up to 256 MiB).
            var bytes = output.GetBuffer();
            var length = checked((int)output.Length);
            var offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, offset, length - offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    private sealed record CachedResponse(
        TarkovDevCacheEntry Entry,
        bool IsFromCache,
        bool IsStale,
        string? RefusalReason = null);

    private sealed record DatasetCardinality(string Name, long Count);
}

internal sealed class TarkovDevDatasetRefusalMarker(string reason, Exception innerException)
    : Exception(reason, innerException)
{
    public string Reason { get; } = reason;
}
