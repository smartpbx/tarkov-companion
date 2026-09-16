using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.Infrastructure.Persistence.Inventory;

/// <summary>Reads the durable V2 recognition root, then projects only positively observed holdings.</summary>
public sealed class SqliteObservedInventoryEvidenceReader(
    SqliteV2DataStore dataStore,
    ObservedInventoryRecognitionProjector projector) : IObservedInventoryEvidenceReader
{
    public async Task<ObservedInventoryEvidenceSnapshot?> ReadCurrentAsync(
        InventoryProfileScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stored = await dataStore.ReadCurrentInventoryAsync(
            scope.ProfileId,
            scope.Generation,
            scope.GameMode,
            cancellationToken).ConfigureAwait(false);
        return stored is null ? null : projector.Project(stored);
    }
}

/// <summary>
/// Stitches overlapping stash captures by container coordinate. Conflicting cells and unplaced
/// regions reduce completeness; neither can inflate the holdings supplied to recommendations.
/// </summary>
public sealed class ObservedInventoryRecognitionProjector
{
    private const string ProjectorVersion = "inventory-observation-1";

    public ObservedInventoryEvidenceSnapshot Project(ObservedInventorySnapshot stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var recognition = stored.Recognition ?? throw new InvalidDataException("An inventory snapshot has no recognition root.");
        var stash = recognition.Result.Value ?? throw new InvalidDataException("An inventory snapshot has no recognized stash value.");
        var cells = new Dictionary<CellKey, CellObservation>();
        var conflicts = new HashSet<CellKey>();
        var incomplete = recognition.Result.Status.Completeness != ResultCompleteness.Complete;
        var additionalUnresolved = 0;

        foreach (var region in stash.CapturedRegions.OrderBy(region => region.CaptureOrdinal))
        {
            if (region.OriginInContainer.Value is not { } origin ||
                region.OriginInContainer.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
            {
                incomplete = true;
                additionalUnresolved = SaturatingIncrement(additionalUnresolved, region.Grid.Cells.Count);
                continue;
            }

            foreach (var cell in region.Grid.Cells)
            {
                var key = new CellKey(
                    region.ContainerPath,
                    origin.Row + cell.Anchor.Row,
                    origin.Column + cell.Anchor.Column);
                if (conflicts.Contains(key))
                {
                    continue;
                }

                var item = cell.Item.Value;
                var itemId = item?.CanonicalId.Value;
                if (item is null || string.IsNullOrWhiteSpace(itemId))
                {
                    incomplete = true;
                    additionalUnresolved = SaturatingIncrement(additionalUnresolved, 1);
                    continue;
                }

                var observation = new CellObservation(
                    itemId.Trim(),
                    item.Quantity.Value,
                    item.FoundInRaid.Value);
                if (!cells.TryAdd(key, observation) && cells[key] != observation)
                {
                    // Do not choose between two different readings of the same physical anchor.
                    cells.Remove(key);
                    conflicts.Add(key);
                    incomplete = true;
                    additionalUnresolved = SaturatingIncrement(additionalUnresolved, 1);
                }
            }
        }

        var countProvenance = ProjectionProvenance(recognition.Result.Provenance, stored.RecordedUtc);
        var items = cells.Values
            .GroupBy(cell => cell.ItemId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => ProjectItem(group.Key, group.ToArray(), countProvenance, recognition.Result.Status.Freshness))
            .ToArray();
        var coverage = ProjectCoverage(stash.Coverage);
        var unresolved = SumUnresolved(stash.UnresolvedCells.Value, additionalUnresolved);
        var complete = !incomplete &&
                       coverage.Fraction == 1 &&
                       unresolved == 0 &&
                       items.All(item =>
                           item.TotalQuantity.Status.Completeness == ResultCompleteness.Complete &&
                           item.FoundInRaidQuantity.Status.Completeness == ResultCompleteness.Complete);
        var status = new ResultStatus(
            complete ? ResultCompleteness.Complete : ResultCompleteness.Partial,
            recognition.Result.Status.Freshness,
            complete ? "inventory-observation.complete" : "inventory-observation.partial",
            incomplete || unresolved is null or > 0
                ? "Unknown, unplaced, or conflicting cells were excluded from exact holdings."
                : null);

        return new ObservedInventoryEvidenceSnapshot(
            stored.SnapshotId,
            new InventoryProfileScope(stored.ProfileId, stored.Generation, stored.GameMode),
            stash.SnapshotId,
            status,
            coverage,
            recognition.Result.Provenance,
            items,
            unresolved);
    }

    private static ObservedItemCount ProjectItem(
        string itemId,
        IReadOnlyList<CellObservation> observations,
        EvidenceProvenance provenance,
        FreshnessState freshness)
    {
        var knownQuantities = observations.Where(item => item.Quantity is not null).ToArray();
        var total = knownQuantities.Length == 0
            ? (int?)null
            : SaturatingSum(knownQuantities.Select(item => item.Quantity!.Value));
        var totalComplete = knownQuantities.Length == observations.Count;
        var knownFir = knownQuantities.Where(item => item.FoundInRaid is not null).ToArray();
        var firTotal = SaturatingSum(knownFir.Where(item => item.FoundInRaid == true).Select(item => item.Quantity!.Value));
        var firComplete = knownFir.Length == observations.Count;
        int? fir = firComplete || firTotal > 0 ? firTotal : null;

        return new ObservedItemCount(
            itemId,
            CountEvidence(
                $"inventory.{itemId}.total",
                total,
                total is null
                    ? ResultCompleteness.Unknown
                    : totalComplete ? ResultCompleteness.Complete : ResultCompleteness.Partial,
                freshness,
                provenance),
            CountEvidence(
                $"inventory.{itemId}.fir",
                fir,
                fir is null
                    ? ResultCompleteness.Unknown
                    : firComplete ? ResultCompleteness.Complete : ResultCompleteness.Partial,
                freshness,
                provenance));
    }

    private static EvidencedValue<int?> CountEvidence(
        string fieldId,
        int? value,
        ResultCompleteness completeness,
        FreshnessState freshness,
        EvidenceProvenance provenance) =>
        new(
            fieldId,
            value,
            new ResultStatus(completeness, freshness, $"inventory-count.{completeness.ToString().ToLowerInvariant()}"),
            provenance);

    private static EvidenceCoverage ProjectCoverage(IReadOnlyList<StashContainerCoverage> containers)
    {
        long observed = 0;
        long total = 0;
        var complete = containers.Count > 0;
        foreach (var container in containers)
        {
            if (container.ObservedCells.Value is not { } observedCells ||
                container.TotalCells.Value is not { } totalCells)
            {
                complete = false;
                continue;
            }

            observed += observedCells;
            total += totalCells;
        }

        var fraction = complete && total > 0
            ? Math.Clamp((double)observed / total, 0, 1)
            : (double?)null;
        return new EvidenceCoverage(
            sampleSize: observed,
            fraction: fraction,
            description: complete
                ? "Fraction of declared stash container cells captured."
                : "Some container coverage is unknown; the sample size counts only declared observed cells.");
    }

    private static EvidenceProvenance ProjectionProvenance(
        EvidenceProvenance source,
        DateTimeOffset recordedUtc)
    {
        var generatedUtc = recordedUtc < source.EvidenceThroughUtc
            ? source.EvidenceThroughUtc
            : recordedUtc;
        var producer = new ProducerIdentity(
            "Tarkov Companion inventory projector",
            ProjectorVersion,
            ContainsModelledEstimate(source) ? ProjectorVersion : null);
        if (ContainsModelledEstimate(source))
        {
            return new EvidenceProvenance(
                EvidenceSourceClass.ModelledEstimate,
                "inventory.observed-counts",
                generatedUtc,
                source.Confidence,
                producer,
                dataThroughUtc: source.EvidenceThroughUtc,
                generatedUtc: generatedUtc,
                coverage: source.Coverage ?? new EvidenceCoverage(description: "Recognition-root coverage was not quantified."),
                inputs: [source]);
        }

        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "inventory.observed-counts",
            generatedUtc,
            source.Confidence,
            producer,
            generatedUtc: generatedUtc,
            coverage: source.Coverage,
            inputs: [source]);
    }

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static int? SumUnresolved(int? reported, int additional)
    {
        if (reported is null)
        {
            return additional == 0 ? null : additional;
        }

        return reported > int.MaxValue - additional ? int.MaxValue : reported + additional;
    }

    private static int SaturatingIncrement(int value, int increment) =>
        increment > int.MaxValue - value ? int.MaxValue : value + increment;

    private static int SaturatingSum(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total = value > int.MaxValue - total ? int.MaxValue : total + value;
        }

        return total;
    }

    private readonly record struct CellKey(string ContainerPath, int Row, int Column);

    private sealed record CellObservation(string ItemId, int? Quantity, bool? FoundInRaid);
}
