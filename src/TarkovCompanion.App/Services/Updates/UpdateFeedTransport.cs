namespace TarkovCompanion.App.Services.Updates;

/// <summary>Where the feed and its packages are read from.</summary>
/// <remarks>
/// Two implementations, and the second is the reason this is an interface: the real one speaks
/// HTTPS to the relay, and the other reads a folder, so the whole update path can run in a test
/// with no network.
/// </remarks>
public interface IUpdateFeedTransport
{
    /// <summary>A description of the place, for a log line and nothing else.</summary>
    string Description { get; }

    /// <summary>The text of one feed document.</summary>
    Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>Copies one package to <paramref name="destination"/>, reporting 0 to 100.</summary>
    Task DownloadAsync(
        string fileName,
        string destination,
        long expectedSize,
        Action<int> progress,
        CancellationToken cancellationToken);
}

/// <summary>The rough channel on the relay: static files over HTTPS.</summary>
public sealed class HttpUpdateFeedTransport : IUpdateFeedTransport
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public HttpUpdateFeedTransport(HttpClient client, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);
        _client = client;
        // A base without its trailing slash resolves "file" against the parent, which would
        // fetch /updates/file instead of /updates/rough/file.
        _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
    }

    public string Description => _baseUri.AbsoluteUri;

    public async Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(new Uri(_baseUri, fileName), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > UpdateFeedDocument.MaximumLength)
        {
            throw new UpdateFeedException("The update feed is larger than a feed can be.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        string fileName,
        string destination,
        long expectedSize,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        using var response = await _client
            .GetAsync(new Uri(_baseUri, fileName), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            await UpdateFeedCopy.CopyAsync(source, destination, expectedSize, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>A feed in a folder, for the test harness and for trying a build before it is published.</summary>
public sealed class DirectoryUpdateFeedTransport(string directory) : IUpdateFeedTransport
{
    public string Description => directory;

    public Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Path.Combine(directory, fileName), cancellationToken);

    public async Task DownloadAsync(
        string fileName,
        string destination,
        long expectedSize,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var source = File.OpenRead(Path.Combine(directory, fileName));
        await using (source.ConfigureAwait(false))
        {
            await UpdateFeedCopy.CopyAsync(source, destination, expectedSize, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal static class UpdateFeedCopy
{
    /// <summary>
    /// Copies a package to disk and stops at the size the feed promised.
    /// </summary>
    /// <remarks>
    /// The limit is not the integrity check, the hash is. It is here so that a server sending
    /// without end fills a bounded amount of disk before it is refused, rather than all of it.
    /// </remarks>
    public static async Task CopyAsync(
        Stream source,
        string destination,
        long expectedSize,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > expectedSize)
                {
                    throw new UpdateFeedException(
                        $"The download is larger than the {expectedSize} bytes the feed promised, so it was stopped.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress((int)(total * 100 / expectedSize));
            }
        }
    }
}
