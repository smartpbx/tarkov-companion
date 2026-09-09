using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services;

public interface IDataRefreshOperation
{
    Task<IReadOnlyList<SyncEndpointResult>> RefreshAsync(SyncRequest request, CancellationToken cancellationToken);
}

public sealed class DataSyncService(IDataRefreshOperation refreshOperation, TimeProvider? timeProvider = null) : IDataSyncService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SyncReport> SyncAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);

        var startedUtc = _timeProvider.GetUtcNow();
        var endpoints = await refreshOperation.RefreshAsync(request, cancellationToken).ConfigureAwait(false);
        return new(startedUtc, _timeProvider.GetUtcNow(), endpoints);
    }
}
