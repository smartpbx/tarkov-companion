using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.RecognitionTests;

internal sealed class InMemoryRecognitionCatalogRepository(IReadOnlyList<CanonicalItemReference> items)
    : IRecognitionCatalogRepository
{
    public Task<IReadOnlyList<CanonicalItemReference>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(items);
    }
}
