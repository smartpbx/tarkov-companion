using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class CanonicalItemResolverCache : IInvalidatableProjection, IAsyncDisposable
{
    private readonly IRecognitionCatalogRepository _repository;
    private readonly OcrTextNormalizer _normalizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FuzzyCanonicalItemResolver? _resolver;

    public CanonicalItemResolverCache(
        IRecognitionCatalogRepository repository,
        OcrTextNormalizer? normalizer = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    /// <summary>
    /// Drops the built resolver so the next scan rebuilds it from the rows that exist now.
    /// </summary>
    /// <remarks>
    /// This cache was the one the post-sync block did not invalidate, and it had the worst
    /// consequence of any of them. On a fresh install the resolver is built from an empty item
    /// table, and because it is built once and kept, every scan returned no_match until the
    /// application was restarted — the catalog arriving thirty seconds later changed nothing.
    /// </remarks>
    public void Invalidate() => _resolver = null;

    public async Task<FuzzyCanonicalItemResolver> GetAsync(CancellationToken cancellationToken)
    {
        if (_resolver is not null)
        {
            return _resolver;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolver is null)
            {
                var catalog = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
                _resolver = new(catalog, _normalizer);
            }

            return _resolver;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
