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
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TarkovDevQuestCatalogNormalizer _questCatalogNormalizer = new();

    public async Task<IReadOnlyList<SyncEndpointResult>> RefreshAsync(
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<SyncEndpointResult>(7)
        {
            await RunAsync(
                "items",
                request,
                () => client.GetItemsAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                response => refreshRepository.RefreshItemsAsync(response.Data, _timeProvider.GetUtcNow(), cancellationToken),
                response => response.Data.Items.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "maps",
                request,
                () => client.GetMapsAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                response => refreshRepository.RefreshMapsAsync(response.Data, cancellationToken),
                response => response.Data.Maps.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "tasks",
                request,
                () => client.GetTasksAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                response => refreshRepository.RefreshTasksAsync(
                    _questCatalogNormalizer.Normalize(
                        response,
                        request.GameMode,
                        request.Language,
                        _timeProvider.GetUtcNow()),
                    cancellationToken),
                response => response.Data.Tasks.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "hideout",
                request,
                () => client.GetHideoutAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                response => refreshRepository.RefreshHideoutAsync(response.Data, cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "traders",
                request,
                () => client.GetTradersAsync(request.GameMode, request.Language, request.Force, cancellationToken),
                response => refreshRepository.RefreshTradersAsync(response.Data, cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "crafts",
                request,
                () => client.GetCraftsAsync(request.GameMode, request.Force, cancellationToken),
                response => refreshRepository.RefreshCraftsAsync(response.Data, cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
            await RunAsync(
                "barters",
                request,
                () => client.GetBartersAsync(request.GameMode, request.Force, cancellationToken),
                response => refreshRepository.RefreshBartersAsync(response.Data, cancellationToken),
                response => response.Data.Count,
                cancellationToken).ConfigureAwait(false),
        };

        return results;
    }

    private async Task<SyncEndpointResult> RunAsync<T>(
        string endpoint,
        SyncRequest request,
        Func<Task<TarkovDevResponse<T>>> fetch,
        Func<TarkovDevResponse<T>, Task> persist,
        Func<TarkovDevResponse<T>, int> count,
        CancellationToken cancellationToken)
    {
        var attemptUtc = _timeProvider.GetUtcNow();
        try
        {
            var response = await fetch().ConfigureAwait(false);
            await persist(response).ConfigureAwait(false);
            var status = response.IsStale ? "stale" : "current";
            await syncStateRepository.RecordAsync(
                new(
                    endpoint,
                    ModeSlug(request.GameMode),
                    request.Language.ToLowerInvariant(),
                    response.IsStale ? null : attemptUtc,
                    attemptUtc,
                    response.ETag,
                    response.LastModified,
                    status,
                    null),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.RawSourceJson ?? response.Json))),
                cancellationToken).ConfigureAwait(false);
            return new(endpoint, !response.IsStale, response.IsStale, count(response), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = Summarize(exception);
            await syncStateRepository.RecordAsync(
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
                null,
                cancellationToken).ConfigureAwait(false);
            return new(endpoint, false, false, 0, error);
        }
    }

    private static string Summarize(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";
        return message.Length <= 512 ? message : message[..512];
    }

    private static string ModeSlug(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };
}
