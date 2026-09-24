using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>An item and where its grid image is published.</summary>
public sealed record IconCatalogEntry(string ItemId, Uri ImageUri);

public interface IIconCatalog
{
    Task<IReadOnlyList<IconCatalogEntry>> ListAsync(CancellationToken cancellationToken);
}

public interface IIconContentFetcher
{
    /// <summary>The image's bytes, or null when it could not be had. Never throws for a refusal.</summary>
    Task<byte[]?> FetchAsync(Uri imageUri, CancellationToken cancellationToken);
}

/// <summary>Reads every item's grid image address out of the synced catalog.</summary>
public sealed class SqliteIconCatalog(SqliteConnectionFactory connectionFactory) : IIconCatalog
{
    public async Task<IReadOnlyList<IconCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, image_url FROM items WHERE image_url IS NOT NULL ORDER BY id;";
        var entries = new List<IconCatalogEntry>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Uri.TryCreate(reader.GetString(1), UriKind.Absolute, out var uri) &&
                    HttpIconContentFetcher.IsAllowed(uri))
                {
                    entries.Add(new(reader.GetString(0), uri));
                }
            }
        }
        catch (SqliteException)
        {
            // Before the first migration there is no items table, and so no icons to index.
            return [];
        }

        return entries;
    }
}

/// <summary>Downloads a grid image from json.tarkov.dev's asset host and nowhere else.</summary>
public sealed class HttpIconContentFetcher(HttpClient client) : IIconContentFetcher
{
    private const string AssetHost = "assets.tarkov.dev";

    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, AssetHost, StringComparison.OrdinalIgnoreCase);

    public async Task<byte[]?> FetchAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageUri);
        if (!IsAllowed(imageUri))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client
                .GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > IconContentWriteRequest.MaximumContentBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > IconContentWriteRequest.MaximumContentBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException ||
                                          (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

public sealed record IconIndexReport(int InCatalog, int AlreadyIndexed, int Stored, int Failed, bool StoppedEarly);

/// <summary>
/// Fills the local icon evidence cache from the catalog's grid images, which is what lets a scan
/// name anything at all.
/// </summary>
/// <remarks>
/// <para>
/// The cache (#355) and the grid recognizer (#273) were both merged with nothing feeding the one
/// into the other, so in the running app every occupied cell came back unresolved however well
/// it was measured. This is the missing feeder. It runs after each catalog sync, fetches only
/// what is not already held, and drops the kept reference index so the next scan sees it.
/// </para>
/// <para>
/// ADR 0007 is what permits it: item images may be cached and fingerprinted on the machine that
/// uses them, and neither the images nor anything derived from them is published, bundled or
/// served through the relay. Nothing here does any of the three.
/// </para>
/// </remarks>
public sealed class IconEvidenceIndexer(
    IIconEvidenceCache cache,
    IIconCatalog catalog,
    IIconContentFetcher fetcher,
    IconReferenceIndex index,
    TimeProvider? timeProvider = null,
    ILogger<IconEvidenceIndexer>? logger = null) : IInvalidatableProjection, IAsyncDisposable
{
    private const int Concurrency = 4;
    private const int GiveUpAfterConsecutiveFailures = 25;
    private const int PublishEvery = 500;

    private static readonly ProducerIdentity Producer = new("Tarkov Companion icon indexer", "icon-indexer-1");

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<IconEvidenceIndexer> _logger = logger ?? NullLogger<IconEvidenceIndexer>.Instance;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task _running = Task.CompletedTask;
    private int _disposed;

    /// <summary>The catalog just synced: fetch whatever icons it names that are not held yet.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (!_running.IsCompleted || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _running = Task.Run(async () =>
            {
                try
                {
                    var report = await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Icon index: {InCatalog} in the catalog, {AlreadyIndexed} already held, {Stored} stored, {Failed} failed.",
                        report.InCatalog,
                        report.AlreadyIndexed,
                        report.Stored,
                        report.Failed);

                    // #572: describe every reference now, while nobody is waiting, instead of
                    // inside the first Loot Scan of the session.
                    var started = _timeProvider.GetTimestamp();
                    await index.WarmAsync(_lifetime.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Icon index: reference pictures described in {ElapsedMilliseconds} ms.",
                        (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not refresh the icon index.");
                }
            });
        }
    }

    public async Task<IconIndexReport> RefreshAsync(CancellationToken cancellationToken)
    {
        var entries = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        var held = (await cache.ListEvidenceAsync(cancellationToken).ConfigureAwait(false))
            .Select(evidence => evidence.Key)
            .ToHashSet();
        var missing = entries.Where(entry => !held.Contains(new IconEvidenceKey(entry.ItemId, entry.ImageUri))).ToArray();
        var stored = 0;
        var failed = 0;
        var consecutiveFailures = 0;
        var gaveUp = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(
                    missing,
                    new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = stop.Token },
                    async (entry, token) =>
                    {
                        // Cancelling the loop is cooperative and a worker may already hold its
                        // next entry, so the decision to stop is also checked here.
                        if (Volatile.Read(ref gaveUp) != 0)
                        {
                            return;
                        }

                        var ok = await StoreAsync(entry, token).ConfigureAwait(false);
                        if (ok)
                        {
                            Interlocked.Exchange(ref consecutiveFailures, 0);
                            if (Interlocked.Increment(ref stored) % PublishEvery == 0)
                            {
                                index.Invalidate();
                            }

                            return;
                        }

                        Interlocked.Increment(ref failed);
                        if (Interlocked.Increment(ref consecutiveFailures) >= GiveUpAfterConsecutiveFailures)
                        {
                            // Offline, or the asset host is down: stop asking rather than walk
                            // five thousand timeouts. The next sync tries again.
                            Volatile.Write(ref gaveUp, 1);
                            await stop.CancelAsync().ConfigureAwait(false);
                        }
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref gaveUp) != 0 && !cancellationToken.IsCancellationRequested)
        {
        }

        if (stored > 0)
        {
            index.Invalidate();
        }

        return new(entries.Count, entries.Count - missing.Length, stored, failed, gaveUp != 0);
    }

    private async Task<bool> StoreAsync(IconCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (await fetcher.FetchAsync(entry.ImageUri, cancellationToken).ConfigureAwait(false) is not { } content)
        {
            return false;
        }

        var retrievedUtc = _timeProvider.GetUtcNow();
        try
        {
            await cache.StoreAsync(
                    new IconContentWriteRequest(
                        new IconEvidenceKey(entry.ItemId, entry.ImageUri),
                        retrievedUtc,
                        new EvidenceProvenance(
                            EvidenceSourceClass.PublicStructuredData,
                            entry.ImageUri.AbsoluteUri,
                            retrievedUtc,
                            EvidenceConfidence.Certain,
                            Producer),
                        content),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            // An image the cache will not decode, or has no room for, is simply not indexed.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Registered both as itself and as a projection a sync invalidates, so the container
        // disposes the one instance twice.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task running;
        lock (_gate)
        {
            running = _running;
        }

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }
}
