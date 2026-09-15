using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>Reads an immutable private GitHub release repository with a protected token.</summary>
public sealed partial class AuthenticatedGitHubReleaseFeed : IAuthenticatedReleaseFeed, IDisposable
{
    private const string SourceRepository = "smartpbx/tarkov-companion";
    private readonly HttpClient _client;
    private readonly ReleaseFeedCredentialProvider _credentials;
    private readonly string _repository;
    private readonly SemaphoreSlim _visibilityGate = new(1, 1);
    private bool _privateRepositoryConfirmed;
    private bool _disposed;

    public AuthenticatedGitHubReleaseFeed(
        SignedReleaseFeedOptions options,
        ReleaseFeedCredentialProvider credentials,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ValidateRepository(options.Repository);
        if (string.Equals(options.Repository, SourceRepository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The public source repository cannot be the authenticated release feed.", nameof(options));
        }

        _repository = options.Repository;
        _credentials = credentials;
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        }, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.github.com/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<IReadOnlyList<string>> ListRingAsync(
        string ring,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRing(ring);
        await EnsurePrivateRepositoryAsync(cancellationToken).ConfigureAwait(false);
        using var document = await ReadApiJsonAsync(
            $"repos/{_repository}/contents/rings/{ring}",
            ReleaseFeedLimits.MaximumJsonBytes,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The {ring} ring response is not a directory listing.");
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (names.Count >= ReleaseFeedLimits.MaximumRingEntries)
            {
                throw new InvalidDataException("The release ring exceeds its entry-count limit.");
            }

            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "file" ||
                !item.TryGetProperty("name", out var nameProperty) ||
                nameProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameProperty.GetString()!;
            if (name.Length > 200 || !SafeAssetName().IsMatch(name) || !seen.Add(name))
            {
                throw new InvalidDataException("The release ring contains an unsafe or repeated file name.");
            }

            names.Add(name);
        }

        return names;
    }

    public async Task DownloadRingAsync(
        string ring,
        string name,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRing(ring);
        ValidateAssetName(name);
        ValidateMaximum(maximumBytes);
        await EnsurePrivateRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var token = await _credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            new Uri(_client.BaseAddress!, $"repos/{_repository}/contents/rings/{ring}/{Uri.EscapeDataString(name)}"),
            "application/vnd.github.raw+json",
            token,
            followAuthenticatedRedirect: true,
            cancellationToken).ConfigureAwait(false);
        await WriteResponseAsync(response, destination, maximumBytes, expectedSize: null, expectedSha256: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAssetAsync(
        string buildTag,
        string name,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!BuildTag().IsMatch(buildTag))
        {
            throw new ArgumentException("The release build tag is invalid.", nameof(buildTag));
        }

        ValidateAssetName(name);
        ValidateMaximum(maximumBytes);
        await EnsurePrivateRepositoryAsync(cancellationToken).ConfigureAwait(false);
        using var release = await ReadApiJsonAsync(
            $"repos/{_repository}/releases/tags/{Uri.EscapeDataString(buildTag)}",
            ReleaseFeedLimits.MaximumJsonBytes,
            cancellationToken).ConfigureAwait(false);
        var root = release.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("immutable", out var immutable) || immutable.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String || tag.GetString() != buildTag ||
            !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Build {buildTag} is not a published immutable release.");
        }

        JsonElement? selected = null;
        var count = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            count++;
            if (count > ReleaseFeedLimits.MaximumArtifacts)
            {
                throw new InvalidDataException("The release exceeds its artifact-count limit.");
            }

            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var assetName) &&
                assetName.ValueKind == JsonValueKind.String && assetName.GetString() == name)
            {
                if (selected is not null)
                {
                    throw new InvalidDataException($"Build {buildTag} contains repeated asset {name}.");
                }

                selected = asset.Clone();
            }
        }

        if (selected is not { } selectedAsset ||
            !selectedAsset.TryGetProperty("state", out var state) ||
            state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded" ||
            !selectedAsset.TryGetProperty("size", out var sizeProperty) ||
            !sizeProperty.TryGetInt64(out var expectedSize) || expectedSize is <= 0 || expectedSize > maximumBytes ||
            !selectedAsset.TryGetProperty("digest", out var digestProperty) ||
            digestProperty.ValueKind != JsonValueKind.String ||
            !TryParseDigest(digestProperty.GetString(), out var expectedSha256) ||
            !selectedAsset.TryGetProperty("url", out var urlProperty) ||
            urlProperty.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var assetUrl) ||
            assetUrl.Scheme != Uri.UriSchemeHttps || assetUrl.Host != "api.github.com")
        {
            throw new InvalidDataException($"Build {buildTag} has no bounded, digest-addressed asset {name}.");
        }

        var token = await _credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            assetUrl,
            "application/octet-stream",
            token,
            followAuthenticatedRedirect: true,
            cancellationToken).ConfigureAwait(false);
        await WriteResponseAsync(response, destination, maximumBytes, expectedSize, expectedSha256,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        _visibilityGate.Dispose();
    }

    private async Task EnsurePrivateRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_privateRepositoryConfirmed)
        {
            return;
        }

        await _visibilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_privateRepositoryConfirmed)
            {
                return;
            }

            using var document = await ReadApiJsonAsync(
                $"repos/{_repository}",
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("visibility", out var visibility) ||
                visibility.ValueKind != JsonValueKind.String ||
                visibility.GetString() is not ("private" or "internal"))
            {
                throw new InvalidDataException("The configured release feed is not private or internal.");
            }

            _privateRepositoryConfirmed = true;
        }
        finally
        {
            _visibilityGate.Release();
        }
    }

    private async Task<JsonDocument> ReadApiJsonAsync(
        string relativeUri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var token = await _credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            new Uri(_client.BaseAddress!, relativeUri),
            "application/vnd.github+json",
            token,
            followAuthenticatedRedirect: false,
            cancellationToken).ConfigureAwait(false);
        var bytes = await ReadResponseAsync(response, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ReleaseFeedLimits.MaximumJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub returned malformed release-feed JSON.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        string accept,
        string token,
        bool followAuthenticatedRedirect,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com")
        {
            throw new InvalidOperationException("Authenticated release-feed requests may target only api.github.com.");
        }

        var response = await SendOnceAsync(uri, accept, token, cancellationToken).ConfigureAwait(false);
        for (var redirects = 0; IsRedirect(response.StatusCode); redirects++)
        {
            if (!followAuthenticatedRedirect || redirects >= 2 || response.Headers.Location is not { } location)
            {
                response.Dispose();
                throw new HttpRequestException("The release feed returned an unexpected redirect.");
            }

            var redirected = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (redirected.Scheme != Uri.UriSchemeHttps || !IsGitHubContentHost(redirected.Host))
            {
                response.Dispose();
                throw new HttpRequestException("The release feed redirected outside GitHub's content hosts.");
            }

            response.Dispose();
            uri = redirected;
            // The redirect URL is short-lived and was obtained through the authenticated API.
            // Never forward the feed credential to the content host.
            response = await SendOnceAsync(uri, accept, token: null, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The private release feed returned HTTP {(int)status}.", null, status);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Uri uri,
        string accept,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd("TarkovCompanion-V2-Updater/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadResponseAsync(
        HttpResponseMessage response,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && (length <= 0 || length > maximumBytes))
        {
            throw new InvalidDataException("The release-feed response is outside its byte limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (output.Length == 0)
        {
            throw new InvalidDataException("The release feed returned an empty response.");
        }

        return output.ToArray();
    }

    private static async Task WriteResponseAsync(
        HttpResponseMessage response,
        string destination,
        long maximumBytes,
        long? expectedSize,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (response.Content.Headers.ContentLength is { } contentLength &&
            (contentLength <= 0 || contentLength > maximumBytes ||
             expectedSize is not null && contentLength != expectedSize))
        {
            throw new InvalidDataException("The release asset's declared size is invalid.");
        }

        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The release destination has no parent directory."));
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // Refuse an occupied destination before entering the partial-download cleanup scope. A
        // CreateNew failure means the file belongs to the caller; deleting it would turn a safe
        // no-overwrite refusal into data loss.
        var output = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await using var ownedOutput = output;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var written = await CopyBoundedAsync(input, ownedOutput, maximumBytes, cancellationToken, hash)
                .ConfigureAwait(false);
            await ownedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (written == 0 || expectedSize is not null && written != expectedSize ||
                expectedSha256 is not null &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded release asset does not match its authenticated metadata.");
            }
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken,
        IncrementalHash? hash = null)
    {
        var rented = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long written = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return written;
                }

                written = checked(written + read);
                if (written > maximumBytes)
                {
                    throw new InvalidDataException("The release-feed response exceeded its byte limit.");
                }

                hash?.AppendData(rented, 0, read);
                await output.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsGitHubContentHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDigest(string? value, out string digest)
    {
        const string prefix = "sha256:";
        digest = value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : string.Empty;
        return LowerHex64().IsMatch(digest);
    }

    private static void ValidateRepository(string repository)
    {
        if (!Repository().IsMatch(repository ?? string.Empty))
        {
            throw new ArgumentException("The release repository must be owner/name.", nameof(repository));
        }
    }

    private static void ValidateRing(string ring)
    {
        if (ring is not ("canary" or "beta" or "stable"))
        {
            throw new ArgumentException("The release ring is invalid.", nameof(ring));
        }
    }

    private static void ValidateAssetName(string name)
    {
        if (name is null || name.Length > 200 || !SafeAssetName().IsMatch(name))
        {
            throw new ArgumentException("The release asset name is unsafe.", nameof(name));
        }
    }

    private static void ValidateMaximum(long maximumBytes)
    {
        if (maximumBytes is <= 0 or > ReleaseFeedLimits.MaximumArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Repository();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAssetName();

    [GeneratedRegex("^v2-build-(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildTag();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();
}
