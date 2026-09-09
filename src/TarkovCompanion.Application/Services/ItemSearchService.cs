using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Application.Services;

public sealed class ItemSearchService(IItemRepository repository) : IItemSearchService
{
    public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return repository.SearchAsync(query, limit, cancellationToken);
    }
}
