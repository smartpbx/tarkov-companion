using System.Text.Json;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

public sealed record TarkovDevLootSpawnRefreshResult(
    LootSpawnSourceImportDisposition Disposition,
    TarkovDevLootSpawnNormalizationResult? Published,
    LootSpawnSourceBundle? LastKnownGood,
    IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics);

/// <summary>
/// Production composition for the three already-governed inputs used by the loot layer.
/// </summary>
/// <remarks>
/// The source clients retain their own bounded caches. This service serially obtains maps, items,
/// and the reviewed map catalog, normalizes their exact response documents, then advances the
/// atomic publication head only after the complete candidate validates. A refused fetch,
/// transform, or publication records one bounded quarantine reason and leaves the last-known-good
/// bundle visible. One instance and publication store are bound to one game mode and language so a
/// profile switch cannot retain a different mode's head. It performs no game process, input,
/// renderer, or network-traffic inspection.
/// </remarks>
public sealed class TarkovDevLootSpawnRefreshService : ILootSpawnSourceRefreshService
{
    private readonly GameMode _gameMode;
    private readonly string _language;
    private readonly TarkovDevJsonClient _jsonClient;
    private readonly TarkovDevMapCatalogClient _mapCatalogClient;
    private readonly TarkovDevLootSpawnNormalizer _normalizer;
    private readonly ILootSpawnSourcePublicationStore _publicationStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public TarkovDevLootSpawnRefreshService(
        GameMode gameMode,
        string language,
        TarkovDevJsonClient jsonClient,
        TarkovDevMapCatalogClient mapCatalogClient,
        TarkovDevLootSpawnNormalizer normalizer,
        ILootSpawnSourcePublicationStore publicationStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _gameMode = gameMode;
        _language = language;
        _jsonClient = jsonClient ?? throw new ArgumentNullException(nameof(jsonClient));
        _mapCatalogClient = mapCatalogClient ?? throw new ArgumentNullException(nameof(mapCatalogClient));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _publicationStore = publicationStore ?? throw new ArgumentNullException(nameof(publicationStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<TarkovDevLootSpawnRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    async ValueTask<LootSpawnSourceImportResult> ILootSpawnSourceRefreshService.RefreshAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        var result = await RefreshAsync(force, cancellationToken).ConfigureAwait(false);
        return new(
            result.Disposition,
            result.Published?.Bundle,
            result.LastKnownGood,
            result.Diagnostics);
    }

    private async ValueTask<TarkovDevLootSpawnRefreshResult> RefreshCoreAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detectedUtc = UtcNow();
        try
        {
            // Deliberately serial: both endpoint bodies are retained for exact composite hashing,
            // so overlapping their largest allocations buys little and increases peak memory.
            var maps = await _jsonClient
                .GetMapsAsync(_gameMode, _language, force, cancellationToken)
                .ConfigureAwait(false);
            var items = await _jsonClient
                .GetItemsAsync(_gameMode, _language, force, cancellationToken)
                .ConfigureAwait(false);
            var mapCatalog = await _mapCatalogClient.GetAsync(cancellationToken).ConfigureAwait(false);
            if (mapCatalog.Catalog is null || string.IsNullOrWhiteSpace(mapCatalog.SourceJson))
            {
                throw new LootSpawnSourceImportException(
                    "map-catalog.unavailable",
                    "No reviewed map catalog or last-known-good catalog cache is available.");
            }

            detectedUtc = UtcNow();
            var normalized = await _normalizer.NormalizeAsync(
                new(_gameMode, _language, detectedUtc, maps, items, mapCatalog.SourceJson, mapCatalog.Catalog),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _publicationStore.PublishAsync(normalized.Bundle, cancellationToken).ConfigureAwait(false);
            var partial = normalized.Bundle.Diagnostics.Count > 0 ||
                          normalized.Bundle.Snapshots.Any(snapshot =>
                              snapshot.Status.Completeness != ResultCompleteness.Complete);
            return new(
                partial
                    ? LootSpawnSourceImportDisposition.PublishedPartial
                    : LootSpawnSourceImportDisposition.Published,
                normalized,
                normalized.Bundle,
                normalized.Bundle.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsLocalOnly(exception))
        {
            // [#292] Local only is a choice, not a failure: nothing is quarantined, and the panel
            // and Setup say "Local only · off" instead of "the refresh failed".
            cancellationToken.ThrowIfCancellationRequested();
            var lastKnownGood = await _publicationStore
                .ReadLastKnownGoodAsync(cancellationToken)
                .ConfigureAwait(false);
            return new(
                LootSpawnSourceImportDisposition.SkippedLocalOnly,
                null,
                lastKnownGood,
                [new LootSpawnSourceDiagnostic(
                    LootSpawnRefreshCodes.LocalOnly,
                    "Local only is on, so no loot-spawn data was downloaded.")]);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = exception is LootSpawnSourceImportException refusal
                ? new LootSpawnSourceDiagnostic(refusal.Code, RefusalDetail(refusal.Code))
                : new LootSpawnSourceDiagnostic(
                    "source.refresh-failed",
                    "The bounded production source refresh failed; the last-known-good loot snapshot was retained.");
            await _publicationStore.QuarantineAsync(diagnostic, detectedUtc, cancellationToken).ConfigureAwait(false);
            var lastKnownGood = await _publicationStore
                .ReadLastKnownGoodAsync(cancellationToken)
                .ConfigureAwait(false);
            return new(
                LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
                null,
                lastKnownGood,
                [diagnostic]);
        }
    }

    private DateTimeOffset UtcNow()
    {
        var value = _timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }

    /// <summary>The offline probe (Local only, or TARKOV_COMPANION_OFFLINE) or the network policy said no.</summary>
    internal static bool IsLocalOnly(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TarkovDevOfflineException or
                TarkovCompanion.Application.Services.Network.NetworkBlockedException
                {
                    Verdict: TarkovCompanion.Core.Network.NetworkVerdict.LocalOnly,
                })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRecoverable(Exception exception) => exception is
        LootSpawnSourceImportException or
        TarkovDevRequestException or
        TarkovDevResponseBudgetException or
        TarkovDevOfflineException or
        HttpRequestException or
        IOException or
        InvalidDataException or
        JsonException;

    private static string RefusalDetail(string code) => code switch
    {
        "publication.source-conflict" or
        "publication.import-regression" or
        "publication.superseded" or
        "publication.evidence-regression" or
        "publication.coverage-regression" or
        "publication.item-evidence-regression" or
        "publication.identity-conflict" or
        "publication.item-evidence-conflict" =>
            "The candidate publication would regress or conflict with the last-known-good head " +
            "and was retained only as quarantine evidence.",
        "publication.store-busy" =>
            "Another process retained the durable loot publication lease; the existing last-known-good head was not changed.",
        _ => "The production source candidate failed bounded validation; the last-known-good loot snapshot was retained.",
    };
}
