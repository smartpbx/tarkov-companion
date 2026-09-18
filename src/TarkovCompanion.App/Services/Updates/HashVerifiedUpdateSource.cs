using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>The bytes that arrived are not the bytes the feed described.</summary>
public sealed class UpdateHashMismatchException(string fileName, string expected, string actual)
    : Exception($"{fileName} does not match the feed. Expected SHA256 {expected}, downloaded {actual}. It was not installed.")
{
    public string FileName { get; } = fileName;

    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>
/// Hands the updater its feed and its packages, and refuses a package that does not match the feed.
/// </summary>
/// <remarks>
/// What this proves is narrow and worth stating exactly: the file on disk is the file the feed
/// described. The feed itself is trusted because it came over TLS from the relay, and for no
/// other reason. There is no signature and no publisher identity anywhere in this path, so
/// somebody who can write to the relay's feed folder can publish a build. The signed ring
/// (docs/RELEASES.md, #280) is what closes that, and replaces this.
///
/// The updater library checks a checksum too. This check exists anyway because that one accepts
/// SHA1 when a feed has no SHA256, says nothing to our log, and is not something a test here can
/// hold still. The refusal below is ours: both hashes are logged, the file is deleted, and the
/// exception carries a sentence the settings page can show as it is.
/// </remarks>
public sealed class HashVerifiedUpdateSource : IUpdateSource
{
    private readonly IUpdateFeedTransport _transport;
    private readonly ILogger? _logger;
    private UpdateFeedDocument? _feed;

    public HashVerifiedUpdateSource(IUpdateFeedTransport transport, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _logger = logger;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var json = await _transport.ReadTextAsync($"releases.{channel}.json", CancellationToken.None)
            .ConfigureAwait(false);
        // Ours first: a feed this refuses never reaches the updater at all.
        _feed = UpdateFeedDocument.Parse(json);
        _logger?.LogInformation(
            "Read the update feed at {Feed}: {Count} package(s)",
            _transport.Description,
            _feed.Packages.Count);
        return VelopackAssetFeed.FromJson(json);
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseEntry);
        var expected = _feed?.Find(releaseEntry.FileName)
            ?? throw new UpdateFeedException($"{releaseEntry.FileName} is not in the feed that was checked.");

        try
        {
            await _transport
                .DownloadAsync(expected.FileName, localFile, expected.Size, progress, cancelToken)
                .ConfigureAwait(false);
            var actual = await HashAsync(localFile, cancelToken).ConfigureAwait(false);
            if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError(
                    "Refused {File}: the feed says SHA256 {Expected} and the download is {Actual}",
                    expected.FileName,
                    expected.Sha256,
                    actual);
                throw new UpdateHashMismatchException(expected.FileName, expected.Sha256, actual);
            }

            _logger?.LogInformation(
                "Verified {File}: the feed says SHA256 {Expected} and the download is {Actual}",
                expected.FileName,
                expected.Sha256,
                actual);
        }
        catch
        {
            // Nothing half-fetched or refused is left where a later run could mistake it for a
            // package that passed.
            TryDelete(localFile);
            throw;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The refusal already happened and is what the caller hears about.
        }
    }
}
