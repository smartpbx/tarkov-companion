using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevDataRefreshOperation(
    TarkovDevJsonClient client,
    SqliteDataRefreshRepository refreshRepository,
    SqliteSyncStateRepository syncStateRepository,
    TimeProvider? timeProvider = null) : IDataRefreshOperation
{
    private static readonly string[] EndpointKeys =
        ["items", "maps", "tasks", "hideout", "traders", "crafts", "barters"];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TarkovDevQuestCatalogNormalizer _questCatalogNormalizer = new();

    public async Task<IReadOnlyList<SyncEndpointResult>> RefreshAsync(
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        var run = await syncStateRepository.BeginRunAsync(
            ModeSlug(request.GameMode),
            request.Language.ToLowerInvariant(),
            EndpointKeys,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return new List<SyncEndpointResult>(7)
            {
                await RunAsync(
                "items",
                run,
                request,
                () => client.GetItemsAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshItemsWithCommitAsync(
                    response.Data,
                    _timeProvider.GetUtcNow(),
                    commitAction,
                    cancellationToken),
                response => response.Data.Items.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "maps",
                run,
                request,
                () => client.GetMapsAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshMapsWithCommitAsync(
                    response.Data,
                    commitAction,
                    cancellationToken),
                response => response.Data.Maps.Count,
                cancellationToken,
                // Extracts default to an empty list when upstream renames the property, so a
                // payload with the right number of maps and no exits on any of them looks
                // healthy by count alone and is the one shape that would quietly break the
                // extract panel on every map at once.
                response => response.Data.Maps.Count > 0 && response.Data.Maps.Values.All(map => map.Extracts.Count == 0)
                    ? "every map came back with no extracts"
                    : null).ConfigureAwait(false),
            await RunAsync(
                "tasks",
                run,
                request,
                () => client.GetTasksAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshTasksWithCommitAsync(
                    _questCatalogNormalizer.Normalize(
                        response,
                        request.GameMode,
                        request.Language,
                        _timeProvider.GetUtcNow()),
                    commitAction,
                    cancellationToken),
                response => response.Data.Tasks.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "hideout",
                run,
                request,
                () => client.GetHideoutAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshHideoutWithCommitAsync(
                    response.Data,
                    commitAction,
                    cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "traders",
                run,
                request,
                () => client.GetTradersAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshTradersWithCommitAsync(
                    response.Data,
                    commitAction,
                    cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "crafts",
                run,
                request,
                () => client.GetCraftsAsync(request.GameMode, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshCraftsWithCommitAsync(
                    response.Data,
                    commitAction,
                    cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "barters",
                run,
                request,
                () => client.GetBartersAsync(request.GameMode, request.Force, cancellationToken),
                (response, commitAction) => refreshRepository.RefreshBartersWithCommitAsync(
                    response.Data,
                    commitAction,
                    cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            };
        }
        finally
        {
            // A cancelled half-run remains explicit partial evidence. Recovery is a short local
            // write and must not be skipped merely because the caller's token initiated it.
            await syncStateRepository.CompleteRunAsync(run.RunId, _timeProvider.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<SyncEndpointResult> RunAsync<T>(
        string endpoint,
        DatasetSyncRunLease run,
        SyncRequest request,
        Func<Task<TarkovDevResponse<T>>> fetch,
        Func<TarkovDevResponse<T>, DataRefreshCommitAction, Task> persist,
        Func<TarkovDevResponse<T>, int> count,
        CancellationToken cancellationToken,
        Func<TarkovDevResponse<T>, string?>? sanityCheck = null)
    {
        var attemptUtc = _timeProvider.GetUtcNow();
        try
        {
            var response = await fetch().ConfigureAwait(false);
            var responseCount = count(response);
            var refusal = response.RefusalReason ??
                await RefusalReasonAsync(endpoint, responseCount, sanityCheck?.Invoke(response), cancellationToken)
                    .ConfigureAwait(false);
            if (refusal is not null)
            {
                await syncStateRepository.RecordAsync(
                    new(
                        endpoint,
                        ModeSlug(request.GameMode),
                        request.Language.ToLowerInvariant(),
                        null,
                        attemptUtc,
                        null,
                        null,
                        "refused",
                        refusal),
                    null,
                    cancellationToken,
                    run,
                    0).ConfigureAwait(false);
                return new(endpoint, false, false, 0, refusal);
            }

            var status = response.IsStale ? "stale" : "current";
            var state = new SyncStateEntry(
                endpoint,
                ModeSlug(request.GameMode),
                request.Language.ToLowerInvariant(),
                response.IsStale ? null : attemptUtc,
                attemptUtc,
                response.ETag,
                response.LastModified,
                status,
                null);
            // The published rows come from the translated/merged document. Hashing only the
            // untranslated source allowed two languages (or two translation generations) to
            // expose different normalized rows under the same publication identity.
            var contentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(response.Json)));
            await persist(
                response,
                (connection, transaction, commitCancellation) =>
                    SqliteSyncStateRepository.RecordInTransactionAsync(
                        state,
                        contentHash,
                        run,
                        responseCount,
                        connection,
                        transaction,
                        commitCancellation)).ConfigureAwait(false);
            return new(endpoint, !response.IsStale, response.IsStale, responseCount, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatasetPublicationSupersededException exception)
        {
            return Superseded(endpoint, exception);
        }
        catch (TarkovDevDatasetRefusedException exception)
        {
            var refusal = Summarize(exception);
            return await RecordTerminalAsync(
                endpoint,
                run,
                new(
                    endpoint,
                    ModeSlug(request.GameMode),
                    request.Language.ToLowerInvariant(),
                    null,
                    attemptUtc,
                    null,
                    null,
                    "refused",
                    refusal),
                refusal,
                cancellationToken,
                0).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var error = Summarize(exception);
            return await RecordTerminalAsync(
                endpoint,
                run,
                new(
                    endpoint,
                    ModeSlug(request.GameMode),
                    request.Language.ToLowerInvariant(),
                    null,
                    attemptUtc,
                    null,
                    null,
                    "failed",
                    error),
                error,
                cancellationToken,
                0).ConfigureAwait(false);
        }
    }

    private async Task<SyncEndpointResult> RecordTerminalAsync(
        string endpoint,
        DatasetSyncRunLease run,
        SyncStateEntry state,
        string error,
        CancellationToken cancellationToken,
        int recordCount)
    {
        try
        {
            await syncStateRepository.RecordAsync(
                state,
                null,
                cancellationToken,
                run,
                recordCount).ConfigureAwait(false);
            return new(endpoint, false, false, recordCount, error);
        }
        catch (DatasetPublicationSupersededException exception)
        {
            // Recording a failed/refused result also changes the scoped head. An older run must
            // not regain authority through its error path after its normalized commit was
            // correctly rejected.
            return Superseded(endpoint, exception);
        }
    }

    private static SyncEndpointResult Superseded(
        string endpoint,
        DatasetPublicationSupersededException exception) =>
        new(endpoint, false, false, 0, $"Superseded: {exception.Message}");

    private static string Summarize(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";
        return message.Length <= 512 ? message : message[..512];
    }

    /// <summary>
    /// Why this response must not be persisted, or null to go ahead.
    /// </summary>
    /// <remarks>
    /// Every refresh in this repository replaces its table wholesale, and the validators pass
    /// trivially on an empty payload — ValidateItems iterates the incoming dictionary, so an
    /// empty one satisfies it — so one bad response emptied the catalog and every page then
    /// said, accurately, that there was no data. DescribeEmptyRefresh noticed for items alone,
    /// and only after the rows were already gone.
    ///
    /// Half is the threshold because the catalog does shrink legitimately: a wipe retires
    /// items, a map leaves rotation. Losing half of it in one sync is not that.
    /// </remarks>
    private async Task<string?> RefusalReasonAsync(
        string endpoint,
        int incoming,
        string? sanityFailure,
        CancellationToken cancellationToken)
    {
        if (sanityFailure is { Length: > 0 })
        {
            return $"Refused: {sanityFailure}.";
        }

        var existing = await refreshRepository.CountRowsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (existing is not { } held || held == 0)
        {
            // Nothing to protect. A first run legitimately goes from nothing to everything.
            return null;
        }

        if (incoming == 0)
        {
            return $"Refused: the response held no {endpoint} and {held:N0} are already stored.";
        }

        return incoming * 2 < held
            ? $"Refused: the response held {incoming:N0} {endpoint} against {held:N0} already stored."
            : null;
    }

    private static string ModeSlug(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };
}
