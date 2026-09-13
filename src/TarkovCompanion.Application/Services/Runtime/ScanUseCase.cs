using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Runtime;

public interface IRuntimeScanUseCase
{
    Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IScanAdapter
{
    Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken);
}

public sealed class ScanUseCase(
    IScanAdapter adapter,
    IRuntimeStateStore stateStore,
    RaidActivityCoordinator raidActivityCoordinator) : IRuntimeScanUseCase
{
    public async Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = await adapter.ScanAsync(cancellationToken).ConfigureAwait(false);
        stateStore.Update(current => current with { Scan = result });
        if (result.Succeeded)
        {
            await raidActivityCoordinator.RecordScanAsync(result, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

public sealed class UnavailableScanAdapter(TimeProvider? timeProvider = null) : IScanAdapter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ScanExecutionResult.Unavailable(
            "Capture is disabled because no production OCR provider is configured.",
            _timeProvider.GetUtcNow()));
    }
}

public sealed class RecognitionScanAdapter(
    TarkovCompanion.Core.Abstractions.IScanUseCase recognitionScanUseCase) : IScanAdapter
{
    public async Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken)
    {
        var outcome = await recognitionScanUseCase.ScanAsync(
                new(new(
                    "EscapeFromTarkov",
                    Region: null,
                    AllowDesktopFallback: false,
                    Reason: "User-requested local OCR scan.")),
                cancellationToken)
            .ConfigureAwait(false);
        return ScanExecutionResult.FromOutcome(outcome, "local-ocr");
    }
}

/// <summary>
/// Item text alone does not establish found-in-raid state or the other context required
/// for an economic recommendation. Returning no context keeps the recognition result
/// useful without fabricating advice.
/// </summary>
public sealed class EvidenceRequiredScanRecommendationContextProvider : IScanRecommendationContextProvider
{
    public Task<RecommendationContext?> GetAsync(
        ItemDefinition item,
        RecognitionCandidate recognition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(recognition);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<RecommendationContext?>(null);
    }
}

public sealed record FixtureScanOptions(string ItemId, string Source = "demo-fixture");

public sealed class FixtureScanAdapter(
    FixtureScanOptions options,
    IItemRepository itemRepository,
    IRecommendationEngine recommendationEngine,
    TimeProvider? timeProvider = null) : IScanAdapter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken)
    {
        var observedUtc = _timeProvider.GetUtcNow();
        var item = await itemRepository.GetAsync(options.ItemId, cancellationToken).ConfigureAwait(false);
        var price = item is null
            ? null
            : await itemRepository.GetPriceAsync(item.Id, cancellationToken).ConfigureAwait(false);
        if (item is null || price is null)
        {
            return ScanExecutionResult.Unavailable("The configured demo item is missing from the local fixture cache.", observedUtc);
        }

        var context = new RecommendationContext(
            true,
            0,
            0,
            0,
            false,
            EventItemState.Unknown,
            null,
            null,
            Confidence.Certain);
        var recommendation = recommendationEngine.Recommend(item, price, context, ValueTierThresholds.Default);
        return new(
            true,
            true,
            item.Id,
            item.Name,
            recommendation.SelectedEconomicValue,
            recommendation.ValuePerSlot,
            recommendation.Action.ToString(),
            new Confidence(0.96),
            observedUtc,
            options.Source,
            "Resolved by the deterministic demo scan adapter; no live game pixels were used.");
    }
}
