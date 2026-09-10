using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class CanonicalItemResolverCache : IAsyncDisposable
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
