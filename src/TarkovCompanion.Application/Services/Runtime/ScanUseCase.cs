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

    Task<ScanExecutionResult> ExecuteAsync(
        RuntimeScanRequest request,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(request.SourceImagePath)
            ? ExecuteAsync(cancellationToken)
            : throw new InvalidOperationException("This scan provider does not accept an explicit image source.");
}

public sealed record RuntimeScanRequest(string? SourceImagePath = null);

public interface IScanAdapter
{
    Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken);

    Task<ScanExecutionResult> ScanAsync(RuntimeScanRequest request, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(request.SourceImagePath)
            ? ScanAsync(cancellationToken)
            : throw new InvalidOperationException("This scan adapter does not accept an explicit image source.");
}

public sealed class ScanUseCase(
    IScanAdapter adapter,
    IRuntimeStateStore stateStore,
    RaidActivityCoordinator raidActivityCoordinator) : IRuntimeScanUseCase
{
    public Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(new(), cancellationToken);

    public async Task<ScanExecutionResult> ExecuteAsync(
        RuntimeScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await adapter.ScanAsync(request, cancellationToken).ConfigureAwait(false);
        stateStore.Update(current => current with { Scan = result });
        if (result.Outcome?.Extracts is { } extracts)
        {
            await raidActivityCoordinator.ApplyExtractsAsync(
                extracts.Extracts,
                result.Outcome.ObservedUtc,
                cancellationToken).ConfigureAwait(false);
        }

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
    public Task<ScanExecutionResult> ScanAsync(CancellationToken cancellationToken) =>
        ScanAsync(new(), cancellationToken);

    public async Task<ScanExecutionResult> ScanAsync(
        RuntimeScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await recognitionScanUseCase.ScanAsync(
                new(new(
                    "EscapeFromTarkov",
                    Region: null,
                    AllowDesktopFallback: false,
                    Reason: string.IsNullOrWhiteSpace(request.SourceImagePath)
                        ? "User-requested local OCR scan."
                        : "Authenticated developer scan of an explicit synthetic PNG.",
                    SourceImagePath: request.SourceImagePath)),
                cancellationToken)
            .ConfigureAwait(false);
        var selected = outcome.Recognition.Selected;
        var recommendation = outcome.Recommendation;
        var succeeded = outcome.Status != ScanCompletionStatus.Unavailable &&
            (selected is not null ||
             outcome.Recognition.Candidates.Count > 0 ||
             outcome.Extracts?.Observations.Count > 0 ||
             outcome.Container is { } ||
             outcome.Flea?.Listings.Count > 0);
        var available = outcome.Status != ScanCompletionStatus.Unavailable;
        var detail = outcome.Status switch
        {
            ScanCompletionStatus.Unavailable =>
                $"Local OCR scan unavailable ({outcome.DiagnosticCode ?? "no diagnostic"}); no pixels were persisted.",
            _ when selected is not null && recommendation is null =>
                $"Resolved {selected.DisplayName}; recommendation withheld because required item-context evidence was unavailable. No pixels were persisted.",
            _ when selected is not null =>
                $"Resolved {selected.DisplayName} from an in-memory local OCR scan; no pixels were persisted.",
            _ when outcome.Extracts is not null =>
                $"Observed {outcome.Extracts.Observations.Count} extract statuses from local OCR; no pixels were persisted.",
            _ when outcome.Container is not null =>
                $"Observed {outcome.Container.Items.Count} container item groups with " +
                $"{outcome.Container.UnresolvedCells.Count + outcome.Container.AmbiguousCells.Count} flagged cells; no pixels were persisted.",
            _ when outcome.Flea is not null =>
                $"Parsed {outcome.Flea.Listings.Count} visible flea rows locally; no market action was performed and no pixels were persisted.",
            _ =>
                $"{outcome.Context} scan finished with {outcome.Status}; no item was auto-selected and no pixels were persisted.",
        };

        var confidence = selected?.Confidence ??
                         outcome.Container?.Confidence ??
                         outcome.Flea?.Confidence ??
                         outcome.Extracts?.Observations.OrderByDescending(value => value.Confidence.Value).FirstOrDefault()?.Confidence ??
                         outcome.Recognition.Candidates.FirstOrDefault()?.Confidence ??
                         Confidence.Unknown;

        return new(
            available,
            succeeded,
            selected?.CanonicalId,
            selected?.DisplayName,
            outcome.Container?.ApproximateValue ?? recommendation?.SelectedEconomicValue,
            recommendation?.ValuePerSlot,
            recommendation?.Action.ToString(),
            confidence,
            outcome.ObservedUtc.ToUniversalTime(),
            "local-ocr",
            detail,
            outcome);
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
