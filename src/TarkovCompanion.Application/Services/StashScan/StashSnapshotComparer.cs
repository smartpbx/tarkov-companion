using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// Compares two reviewed stash snapshots without treating inference or absence as observation.
/// Movement is reported only when one occurrence on each side has a unique evidence signature;
/// repeated indistinguishable items remain additions/removals rather than invented identity.
/// </summary>
public sealed class StashSnapshotComparer
{
    public StashSnapshotComparison Compare(
        RecognitionResultEnvelope<StashRecognition> previous,
        RecognitionResultEnvelope<StashRecognition> current,
        DateTimeOffset comparedUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (comparedUtc.Offset != TimeSpan.Zero || comparedUtc == default)
        {
            throw new ArgumentException("Comparison time must be a defined UTC instant.", nameof(comparedUtc));
        }

        var previousStash = previous.Result.Value ??
            throw new ArgumentException("The previous snapshot has no stash value.", nameof(previous));
        var currentStash = current.Result.Value ??
            throw new ArgumentException("The current snapshot has no stash value.", nameof(current));
        var before = Flatten(previousStash);
        var after = Flatten(currentStash);
        var changes = new List<StashSnapshotChange>();
        var provenance = ComparisonProvenance(previous.Result.Provenance, current.Result.Provenance, comparedUtc);

        foreach (var identity in before.Select(item => item.Identity)
                     .Concat(after.Select(item => item.Identity))
                     .Distinct()
                     .OrderBy(value => value.ItemId, StringComparer.Ordinal)
                     .ThenBy(value => value.Width)
                     .ThenBy(value => value.Height))
        {
            var oldGroup = before.Where(item => item.Identity == identity).ToList();
            var newGroup = after.Where(item => item.Identity == identity).ToList();

            // Location is the strongest available stable fact for repeated indistinguishable items.
            foreach (var oldItem in oldGroup.ToArray())
            {
                var sameLocation = newGroup.FindIndex(candidate => candidate.Location == oldItem.Location);
                if (sameLocation < 0)
                {
                    continue;
                }

                var newItem = newGroup[sameLocation];
                oldGroup.Remove(oldItem);
                newGroup.RemoveAt(sameLocation);
                AddPairChanges(oldItem, newItem, provenance, changes);
            }

            // A one-to-one remainder supports a movement claim. More than one does not.
            if (oldGroup.Count == 1 && newGroup.Count == 1)
            {
                AddPairChanges(oldGroup[0], newGroup[0], provenance, changes);
                oldGroup.Clear();
                newGroup.Clear();
            }

            foreach (var removed in oldGroup)
            {
                changes.Add(Change(
                    StashSnapshotChangeKind.Removed,
                    removed,
                    null,
                    StashObservationState.Absent,
                    provenance));
            }

            foreach (var added in newGroup)
            {
                changes.Add(Change(
                    StashSnapshotChangeKind.Added,
                    null,
                    added,
                    StashObservationState.Absent,
                    provenance));
            }
        }

        var partial = previous.Result.Status.Completeness != ResultCompleteness.Complete ||
                      current.Result.Status.Completeness != ResultCompleteness.Complete ||
                      provenance.SourceClass == EvidenceSourceClass.Unknown ||
                      before.Any(item => item.State == StashObservationState.Unresolved) ||
                      after.Any(item => item.State == StashObservationState.Unresolved);
        var freshness = previous.Result.Status.Freshness == FreshnessState.Stale ||
                        current.Result.Status.Freshness == FreshnessState.Stale
            ? FreshnessState.Stale
            : previous.Result.Status.Freshness == FreshnessState.Current &&
              current.Result.Status.Freshness == FreshnessState.Current
                ? FreshnessState.Current
                : FreshnessState.Unknown;
        return new StashSnapshotComparison(
            previousStash.SnapshotId,
            currentStash.SnapshotId,
            comparedUtc,
            new ResultStatus(
                partial ? ResultCompleteness.Partial : ResultCompleteness.Complete,
                freshness,
                partial ? "stash.compare.partial" : "stash.compare.complete",
                partial ? "Unplaced, unresolved, or over-bound evidence is kept separate from observed changes." : null),
            changes
                .OrderBy(change => change.ComparisonKey, StringComparer.Ordinal)
                .ThenBy(change => change.Kind)
                .ToArray());
    }

    private static IReadOnlyList<SnapshotEntry> Flatten(StashRecognition stash)
    {
        var result = new List<SnapshotEntry>();
        var placedEntries = new Dictionary<EntryLocation, SnapshotEntry>();
        var conflicts = new HashSet<EntryLocation>();
        foreach (var region in stash.CapturedRegions)
        {
            foreach (var cell in region.Grid.Cells)
            {
                var item = cell.Item.Value;
                var rawItemId = item?.CanonicalId.Value;
                var itemId = string.IsNullOrWhiteSpace(rawItemId)
                    ? null
                    : rawItemId.Trim();
                GridCellAddress? absolute = null;
                if (region.OriginInContainer.Value is { } origin)
                {
                    absolute = new GridCellAddress(
                        origin.Row + cell.Anchor.Row,
                        origin.Column + cell.Anchor.Column);
                }

                var placed = absolute is not null;
                var state = !placed || itemId is null
                    ? StashObservationState.Unresolved
                    : region.OriginInContainer.Provenance.SourceClass is
                        EvidenceSourceClass.GameWrittenScreenshot or EvidenceSourceClass.ExternalVisiblePixels
                        ? StashObservationState.Observed
                        : StashObservationState.Inferred;
                var identity = itemId is null
                    ? new EntryIdentity($"unresolved:{region.ArtifactId}:{cell.Anchor.Row}:{cell.Anchor.Column}", null, null, null, null, null)
                    : new EntryIdentity(
                        itemId,
                        item!.WidthCells.Value,
                        item.HeightCells.Value,
                        item.Rotated.Value,
                        item.FoundInRaid.Value,
                        item.Condition.Value);
                var entry = new SnapshotEntry(
                    identity,
                    Key($"{identity.ItemId}|{region.ContainerPath}|{absolute?.Row}|{absolute?.Column}"),
                    itemId,
                    region.ContainerPath,
                    absolute,
                    item?.Quantity.Value,
                    state);
                if (absolute is null)
                {
                    result.Add(entry);
                    continue;
                }

                if (conflicts.Contains(entry.Location))
                {
                    continue;
                }

                if (!placedEntries.TryGetValue(entry.Location, out var prior))
                {
                    placedEntries.Add(entry.Location, entry);
                    continue;
                }

                if (prior.Identity == entry.Identity && prior.Quantity == entry.Quantity)
                {
                    continue;
                }

                placedEntries.Remove(entry.Location);
                conflicts.Add(entry.Location);
                result.Add(new SnapshotEntry(
                    new EntryIdentity(
                        $"unresolved:conflict:{region.ContainerPath}:{absolute.Value.Row}:{absolute.Value.Column}",
                        null,
                        null,
                        null,
                        null,
                        null),
                    Key($"conflict|{region.ContainerPath}|{absolute.Value.Row}|{absolute.Value.Column}"),
                    null,
                    region.ContainerPath,
                    absolute,
                    null,
                    StashObservationState.Unresolved));
            }
        }

        result.AddRange(placedEntries.Values);
        return result;
    }

    private static void AddPairChanges(
        SnapshotEntry previous,
        SnapshotEntry current,
        EvidenceProvenance provenance,
        ICollection<StashSnapshotChange> changes)
    {
        if (previous.State == StashObservationState.Unresolved && current.State != StashObservationState.Unresolved)
        {
            changes.Add(Change(StashSnapshotChangeKind.Resolved, previous, current, current.State, provenance));
        }
        else if (previous.State != StashObservationState.Unresolved && current.State == StashObservationState.Unresolved)
        {
            changes.Add(Change(StashSnapshotChangeKind.BecameUnresolved, previous, current, current.State, provenance));
        }
        else if (previous.State != current.State)
        {
            changes.Add(Change(StashSnapshotChangeKind.EvidenceChanged, previous, current, current.State, provenance));
        }

        if (previous.Location != current.Location)
        {
            changes.Add(Change(StashSnapshotChangeKind.Moved, previous, current, current.State, provenance));
        }

        if (previous.Quantity != current.Quantity)
        {
            changes.Add(Change(StashSnapshotChangeKind.QuantityChanged, previous, current, current.State, provenance));
        }
    }

    private static StashSnapshotChange Change(
        StashSnapshotChangeKind kind,
        SnapshotEntry? previous,
        SnapshotEntry? current,
        StashObservationState absentSideState,
        EvidenceProvenance provenance)
    {
        var representative = current ?? previous!;
        return new StashSnapshotChange(
            kind,
            representative.ComparisonKey,
            previous?.State ?? absentSideState,
            current?.State ?? absentSideState,
            representative.ItemId,
            previous?.ContainerPath,
            previous?.Anchor,
            previous?.Quantity,
            current?.ContainerPath,
            current?.Anchor,
            current?.Quantity,
            provenance);
    }

    private static EvidenceProvenance ComparisonProvenance(
        EvidenceProvenance previous,
        EvidenceProvenance current,
        DateTimeOffset comparedUtc)
    {
        var inputs = previous == current ? new[] { current } : new[] { previous, current };
        if (inputs.Sum(input => 1 + DescendantInputCount(input)) > EvidenceProvenance.MaxInputCount ||
            inputs.Any(input => ProvenanceDepth(input) >= EvidenceProvenance.MaxInputDepth))
        {
            return new EvidenceProvenance(
                EvidenceSourceClass.Unknown,
                "stash.snapshot-comparison.lineage-limit",
                comparedUtc,
                EvidenceConfidence.Unscored,
                new ProducerIdentity("Tarkov Companion stash comparer", "stash-compare-1"),
                coverage: new EvidenceCoverage(
                    inputs.Length,
                    description: "Compared snapshots whose complete lineage exceeded the bounded provenance contract."));
        }

        var scores = inputs.Select(input => input.Confidence.Score).ToArray();
        var confidence = scores.Any(score => score is null)
            ? EvidenceConfidence.Unscored
            : scores.All(score => score == 1)
                ? EvidenceConfidence.Certain
                : new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scores.Min()!.Value);
        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "stash.snapshot-comparison",
            comparedUtc,
            confidence,
            new ProducerIdentity("Tarkov Companion stash comparer", "stash-compare-1"),
            generatedUtc: comparedUtc,
            coverage: new EvidenceCoverage(inputs.Length, description: "Reviewed snapshots compared."),
            inputs: inputs);
    }

    private static int DescendantInputCount(EvidenceProvenance provenance) =>
        provenance.Inputs.Count + provenance.Inputs.Sum(DescendantInputCount);

    private static int ProvenanceDepth(EvidenceProvenance provenance) =>
        1 + (provenance.Inputs.Count == 0 ? 0 : provenance.Inputs.Max(ProvenanceDepth));

    private static string Key(string value)
    {
        if (value.Length <= 256)
        {
            return value;
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
    }

    private sealed record SnapshotEntry(
        EntryIdentity Identity,
        string ComparisonKey,
        string? ItemId,
        string ContainerPath,
        GridCellAddress? Anchor,
        int? Quantity,
        StashObservationState State)
    {
        public EntryLocation Location { get; } = new(ContainerPath, Anchor);
    }

    private sealed record EntryIdentity(
        string ItemId,
        int? Width,
        int? Height,
        bool? Rotated,
        bool? FoundInRaid,
        ItemConditionReading? Condition);

    private readonly record struct EntryLocation(string ContainerPath, GridCellAddress? Anchor);
}
