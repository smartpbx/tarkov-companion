using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Infrastructure.TarkovTracker;

public sealed class TarkovTrackerApiClient : ITarkovTrackerApiClient, IDisposable
{
    public const string CanonicalOrigin = "https://api.tarkovtracker.org/";

    private static readonly Uri TokenUri = new(new Uri(CanonicalOrigin), "token");
    private static readonly Uri ProgressUri = new(new Uri(CanonicalOrigin), "progress");
    private readonly HttpClient _httpClient;
    private readonly TarkovTrackerOptions _options;
    private readonly TimeProvider _timeProvider;

    public TarkovTrackerApiClient(
        HttpMessageHandler handler,
        TarkovTrackerOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (handler is HttpClientHandler httpClientHandler)
        {
            httpClientHandler.AllowAutoRedirect = false;
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TarkovTrackerTokenValidation> ValidateTokenAsync(
        string token,
        GameMode expectedMode,
        CancellationToken cancellationToken)
    {
        EnsureNetworkEnabled();
        TarkovTrackerTokenPolicy.ValidateForMode(token, expectedMode);
        return await ExecuteBoundedReadAsync(async operationToken =>
        {
            using var request = CreateRequest(TokenUri, token, null);
            using var response = await SendAsync(request, operationToken).ConfigureAwait(false);
            await using var body = await ReadBoundedBodyAsync(response, operationToken).ConfigureAwait(false);
            using var document = ParseDocument(body);
            var root = RequireObject(document.RootElement, "token response");
            RequireSuccess(root, "token response");
            var mode = TarkovTrackerTokenPolicy.ParseApiMode(RequireString(root, "gameMode"));
            if (mode != expectedMode)
            {
                throw InvalidResponse("TarkovTracker token mode does not match the active local profile.");
            }

            var returnedToken = RequireString(root, "token");
            if (!FixedTimeEquals(token, returnedToken))
            {
                throw InvalidResponse("TarkovTracker returned inconsistent token metadata.");
            }

            var permissions = RequireArray(root, "permissions");
            var hasProgressRead = false;
            foreach (var permission in permissions.EnumerateArray())
            {
                if (permission.ValueKind != JsonValueKind.String)
                {
                    throw InvalidResponse("TarkovTracker returned an invalid token permission.");
                }

                hasProgressRead |= permission.ValueEquals("GP");
            }

            if (!hasProgressRead)
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.Forbidden,
                    "The TarkovTracker token does not grant GP progress-read permission.",
                    (int)HttpStatusCode.Forbidden);
            }

            return new TarkovTrackerTokenValidation(mode, ReadQuota(response));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TarkovTrackerProgressFetch> GetProgressAsync(
        string token,
        GameMode expectedMode,
        string? etag,
        CancellationToken cancellationToken)
    {
        EnsureNetworkEnabled();
        TarkovTrackerTokenPolicy.ValidateForMode(token, expectedMode);
        return await ExecuteBoundedReadAsync(async operationToken =>
        {
            using var request = CreateRequest(ProgressUri, token, etag);
            using var response = await SendAsync(request, operationToken, allowNotModified: true)
                .ConfigureAwait(false);
            var responseEtag = response.Headers.ETag?.ToString();
            var quota = ReadQuota(response);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new TarkovTrackerProgressFetch(true, responseEtag ?? etag, null, quota);
            }

            await using var body = await ReadBoundedBodyAsync(response, operationToken).ConfigureAwait(false);
            using var document = ParseDocument(body);
            var root = RequireObject(document.RootElement, "progress response");
            RequireSuccess(root, "progress response");
            var data = RequireObject(RequireProperty(root, "data"), "progress data");
            var meta = RequireObject(RequireProperty(root, "meta"), "progress metadata");
            var mode = TarkovTrackerTokenPolicy.ParseApiMode(RequireString(meta, "gameMode"));
            if (mode != expectedMode)
            {
                throw InvalidResponse("TarkovTracker progress mode does not match the active local profile.");
            }

            var tasks = ParseTasks(RequireArray(data, "tasksProgress"));
            var objectives = ParseObjectives(RequireArray(data, "taskObjectivesProgress"));
            var snapshot = new TarkovTrackerProgressSnapshot(
                mode,
                PayloadHash(mode, tasks, objectives),
                _timeProvider.GetUtcNow().ToUniversalTime(),
                tasks,
                objectives);
            return new TarkovTrackerProgressFetch(false, responseEtag, snapshot, quota);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private void EnsureNetworkEnabled()
    {
        if (!_options.Enabled || !_options.NetworkAccessEnabled)
        {
            throw new InvalidOperationException("The optional TarkovTracker network adapter is disabled.");
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, string token, string? etag)
    {
        EnsureCanonicalUri(uri);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        if (etag is not null)
        {
            if (!EntityTagHeaderValue.TryParse(etag, out var entityTag))
            {
                throw new ArgumentException("The cached TarkovTracker ETag is invalid.", nameof(etag));
            }

            request.Headers.IfNoneMatch.Add(entityTag);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool allowNotModified = false)
    {
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (response.RequestMessage?.RequestUri is { } responseUri)
            {
                EnsureCanonicalUri(responseUri);
            }

            var code = (int)response.StatusCode;
            if (code is >= 300 and <= 399 &&
                !(allowNotModified && response.StatusCode == HttpStatusCode.NotModified))
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.RedirectRejected,
                    "TarkovTracker returned a redirect; credential-bearing redirects are rejected.",
                    code);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.Unauthorized,
                    "TarkovTracker rejected the saved credential; reconnect with a valid mode-scoped token.",
                    code);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.Forbidden,
                    "TarkovTracker denied GP progress-read access.",
                    code);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.RateLimited,
                    "TarkovTracker rate-limited this read; refresh is paused until the server backoff expires.",
                    code,
                    ReadRetryAfter(response));
            }

            if (code >= 500)
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.ServerError,
                    $"TarkovTracker returned HTTP {code}; local progress is unchanged.",
                    code);
            }

            if (!response.IsSuccessStatusCode &&
                !(allowNotModified && response.StatusCode == HttpStatusCode.NotModified))
            {
                throw new TarkovTrackerApiException(
                    TarkovTrackerApiFailure.InvalidResponse,
                    $"TarkovTracker returned unsupported HTTP status {code}; local progress is unchanged.",
                    code);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<T> ExecuteBoundedReadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TarkovTrackerApiException(
                TarkovTrackerApiFailure.Timeout,
                "The TarkovTracker read request timed out.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TarkovTrackerApiException(
                TarkovTrackerApiFailure.Transport,
                "The TarkovTracker read request failed before a response was fully received.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw new TarkovTrackerApiException(
                TarkovTrackerApiFailure.Transport,
                "The TarkovTracker read response could not be read completely.",
                innerException: exception);
        }
    }

    private async Task<MemoryStream> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > _options.MaximumResponseBytes)
        {
            throw InvalidResponse("TarkovTracker returned a response larger than the configured safety limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var destination = new MemoryStream(
            response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
                ? (int)response.Content.Headers.ContentLength.Value
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > _options.MaximumResponseBytes)
                {
                    throw InvalidResponse("TarkovTracker returned a response larger than the configured safety limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            destination.Position = 0;
            return destination;
        }
        catch
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static JsonDocument ParseDocument(MemoryStream body)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException exception)
        {
            throw InvalidResponse("TarkovTracker returned invalid or excessively deep JSON.", exception);
        }
    }

    private static IReadOnlyList<TarkovTrackerTaskProgress> ParseTasks(JsonElement array)
    {
        var values = new List<TarkovTrackerTaskProgress>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            if (values.Count >= 50_000)
            {
                throw InvalidResponse("TarkovTracker returned too many progress records.");
            }

            var record = RequireObject(element, "task progress record");
            var id = RequireId(record);
            if (!ids.Add(id))
            {
                throw InvalidResponse("TarkovTracker returned a duplicate task progress id.");
            }

            values.Add(new(
                id,
                RequireBoolean(record, "complete"),
                OptionalBoolean(record, "failed"),
                OptionalBoolean(record, "invalid")));
        }

        return values;
    }

    private static IReadOnlyList<TarkovTrackerObjectiveProgress> ParseObjectives(JsonElement array)
    {
        var values = new List<TarkovTrackerObjectiveProgress>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            if (values.Count >= 50_000)
            {
                throw InvalidResponse("TarkovTracker returned too many progress records.");
            }

            var record = RequireObject(element, "objective progress record");
            var id = RequireId(record);
            if (!ids.Add(id))
            {
                throw InvalidResponse("TarkovTracker returned a duplicate objective progress id.");
            }

            values.Add(new(
                id,
                RequireBoolean(record, "complete"),
                OptionalCount(record, "count"),
                OptionalBoolean(record, "invalid")));
        }

        return values;
    }

    private static string PayloadHash(
        GameMode mode,
        IReadOnlyList<TarkovTrackerTaskProgress> tasks,
        IReadOnlyList<TarkovTrackerObjectiveProgress> objectives)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("source", "tarkovtracker");
            writer.WriteString("gameMode", TarkovTrackerTokenPolicy.ApiMode(mode));
            writer.WriteStartArray("tasks");
            foreach (var task in tasks.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteBoolean("complete", task.Complete);
                writer.WriteBoolean("failed", task.Failed);
                writer.WriteBoolean("invalid", task.Invalid);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("objectives");
            foreach (var objective in objectives.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", objective.Id);
                writer.WriteBoolean("complete", objective.Complete);
                if (objective.Count is { } count)
                {
                    writer.WriteNumber("count", count);
                }
                else
                {
                    writer.WriteNull("count");
                }

                writer.WriteBoolean("invalid", objective.Invalid);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static TarkovTrackerQuota ReadQuota(HttpResponseMessage response)
    {
        var limit = ReadNonnegativeIntHeader(response, "X-RateLimit-Limit");
        var remaining = ReadNonnegativeIntHeader(response, "X-RateLimit-Remaining");
        var resetSeconds = ReadNonnegativeLongHeader(response, "X-RateLimit-Reset");
        DateTimeOffset? reset = null;
        if (resetSeconds is { } seconds)
        {
            try
            {
                reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                reset = null;
            }
        }

        return new(limit, remaining, reset);
    }

    private DateTimeOffset ReadRetryAfter(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return now + delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date && date > now)
        {
            return date.ToUniversalTime();
        }

        return now + TimeSpan.FromMinutes(1);
    }

    private static int? ReadNonnegativeIntHeader(HttpResponseMessage response, string name)
    {
        var value = ReadSingleHeader(response, name);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static long? ReadNonnegativeLongHeader(HttpResponseMessage response, string name)
    {
        var value = ReadSingleHeader(response, name);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static string? ReadSingleHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static JsonElement RequireProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw InvalidResponse($"TarkovTracker response is missing required field '{name}'.");
        }

        return value;
    }

    private static JsonElement RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse($"TarkovTracker {description} must be a JSON object.");
        }

        return element;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse($"TarkovTracker field '{name}' must be an array.");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse($"TarkovTracker field '{name}' must be a string.");
        }

        return value.GetString() ?? throw InvalidResponse($"TarkovTracker field '{name}' must not be null.");
    }

    private static string RequireId(JsonElement record)
    {
        var id = RequireString(record, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
            !id.Equals(id.Trim(), StringComparison.Ordinal) || id.Any(char.IsControl))
        {
            throw InvalidResponse("TarkovTracker returned an invalid progress id.");
        }

        return id;
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidResponse($"TarkovTracker field '{name}' must be a Boolean."),
        };
    }

    private static bool OptionalBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidResponse($"TarkovTracker field '{name}' must be a Boolean when present."),
        };
    }

    private static decimal? OptionalCount(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var count) || count < 0)
        {
            throw InvalidResponse("TarkovTracker objective count must be a finite nonnegative decimal.");
        }

        return count;
    }

    private static void RequireSuccess(JsonElement root, string description)
    {
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            throw InvalidResponse($"TarkovTracker {description} did not report success.");
        }
    }

    private static void EnsureCanonicalUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("api.tarkovtracker.org", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not "/token" and not "/progress")
        {
            throw new TarkovTrackerApiException(
                TarkovTrackerApiFailure.RedirectRejected,
                "The TarkovTracker request target is outside the canonical HTTPS read-only allowlist.");
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static TarkovTrackerApiException InvalidResponse(string message, Exception? innerException = null) =>
        new(TarkovTrackerApiFailure.InvalidResponse, message, innerException: innerException);
}
