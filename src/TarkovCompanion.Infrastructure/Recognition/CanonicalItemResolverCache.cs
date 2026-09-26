using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Learning;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class CanonicalItemResolverCache : IInvalidatableProjection, IAsyncDisposable
{
    private readonly IRecognitionCatalogRepository _repository;
    private readonly OcrTextNormalizer _normalizer;
    private readonly ICorrectionMemoryStore? _learned;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FuzzyCanonicalItemResolver? _resolver;

    public CanonicalItemResolverCache(
        IRecognitionCatalogRepository repository,
        OcrTextNormalizer? normalizer = null,
        ICorrectionMemoryStore? learned = null)
    {
        _learned = learned;
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
                _resolver = new(await WithLearnedAliasesAsync(catalog, cancellationToken).ConfigureAwait(false), _normalizer);
            }

            return _resolver;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// #712 1-12: a reading the player picked as the same other item twice becomes one more name
    /// for that item, so the next read of it lands there. Nothing learned is not an error.
    /// </summary>
    private async Task<IReadOnlyList<CanonicalItemReference>> WithLearnedAliasesAsync(
        IReadOnlyList<CanonicalItemReference> catalog,
        CancellationToken cancellationToken)
    {
        if (_learned is null)
        {
            return catalog;
        }

        IReadOnlyList<LearnedTextAlias> aliases;
        try
        {
            aliases = await _learned.ListActiveAliasesAsync(LearnedTextAlias.ItemNameKind, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return catalog;
        }

        if (aliases.Count == 0)
        {
            return catalog;
        }

        var byItem = aliases
            .GroupBy(alias => alias.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(alias => alias.NormalizedText).ToArray(), StringComparer.Ordinal);
        return catalog
            .Select(item => byItem.TryGetValue(item.Id, out var learned)
                ? item with { Aliases = [.. item.Aliases ?? [], .. learned] }
                : item)
            .ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
