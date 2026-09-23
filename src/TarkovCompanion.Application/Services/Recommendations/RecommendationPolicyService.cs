using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Recommendations;

public interface IRecommendationPolicyStore
{
    Task<RecommendationHorizonSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(RecommendationHorizonSettings settings, CancellationToken cancellationToken);
}

/// <summary>The single live policy source used by every production recommendation engine.</summary>
public sealed class RecommendationPolicyService(IRecommendationPolicyStore store)
{
    private readonly IRecommendationPolicyStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public event EventHandler<RecommendationHorizonSettings>? Changed;

    public RecommendationHorizonSettings CurrentSettings { get; private set; } = RecommendationHorizonSettings.Default;

    public ExplainableRecommendationPolicy CurrentPolicy =>
        ExplainableRecommendationPolicy.Default.WithHorizons(CurrentSettings);

    public bool IsLoaded { get; private set; }

    public async Task<RecommendationHorizonSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var stored = (await _store.GetAsync(cancellationToken).ConfigureAwait(false)).Normalized();
        IsLoaded = true;
        CurrentSettings = stored;
        Changed?.Invoke(this, stored);
        return stored;
    }

    public async Task UpdateAsync(RecommendationHorizonSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var wanted = settings.Normalized();
        if (IsLoaded && wanted == CurrentSettings)
        {
            return;
        }

        IsLoaded = true;
        CurrentSettings = wanted;
        Changed?.Invoke(this, wanted);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ExplainableRecommendationEngine CreateEngine() => new(CurrentPolicy);
}
