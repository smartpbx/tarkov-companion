using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// A cell the recognizer refused, named by the player from its own lookalikes (#712 1-12).
/// </summary>
/// <remarks>
/// Only an item the recognizer itself offered as a lookalike can be picked, so a correction can
/// never put an item on a cell whose shape it does not fit. The corrected claim says where it
/// came from (<see cref="EvidenceSourceClass.UserEntered"/>) and keeps the cell's region, time and
/// lookalikes, so the verdict built on it is as old as the screenshot, not as new as the tap.
/// </remarks>
public static class GridCellCorrections
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion correction", "grid-correction-1");

    /// <summary>The key a correction of this cell is kept under, beside the frame's content hash.</summary>
    public static string TargetKey(GridCellObservation cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return "cell:" + cell.ObservationId;
    }

    /// <summary>The cell named as <paramref name="itemId"/>, or null when that item was not one of its lookalikes.</summary>
    public static GridCellObservation? Apply(GridCellObservation cell, string itemId)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (cell.Item.Value?.CanonicalId.Value is { } named)
        {
            return string.Equals(named, itemId, StringComparison.Ordinal) ? cell : null;
        }

        var chosen = cell.Item.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Value.CanonicalId.Value ?? candidate.CandidateId, itemId, StringComparison.Ordinal));
        if (chosen is null)
        {
            return null;
        }

        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.UserEntered,
            "recognition.grid.cell.corrected",
            cell.Item.Provenance.ObservedUtc,
            EvidenceConfidence.Certain,
            Producer);
        var item = chosen.Value;
        var corrected = new RecognizedItem(
            Restamp(item.CanonicalId, provenance),
            Restamp(item.DisplayName, provenance),
            Restamp(item.Quantity, provenance),
            Restamp(item.WidthCells, provenance),
            Restamp(item.HeightCells, provenance),
            Restamp(item.Rotated, provenance),
            Restamp(item.FoundInRaid, provenance),
            Restamp(item.Condition, provenance));
        return new GridCellObservation(
            cell.ObservationId,
            cell.Anchor,
            new EvidencedValue<RecognizedItem>(
                cell.Item.FieldId,
                corrected,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "grid.cell.item.corrected"),
                provenance,
                cell.Item.Bounds,
                cell.Item.Candidates));
    }

    /// <summary>
    /// Every kept correction applied to a read, by <see cref="TargetKey"/>. A correction whose
    /// item is not among the cell's lookalikes is ignored, not forced.
    /// </summary>
    public static GridReconstructionRequest Apply(
        GridReconstructionRequest request,
        IReadOnlyDictionary<string, string> corrections)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(corrections);
        if (corrections.Count == 0)
        {
            return request;
        }

        var changed = false;
        var cells = request.OccupiedCells
            .Select(cell =>
            {
                if (corrections.TryGetValue(TargetKey(cell), out var itemId) && Apply(cell, itemId) is { } corrected &&
                    !ReferenceEquals(corrected, cell))
                {
                    changed = true;
                    return corrected;
                }

                return cell;
            })
            .ToArray();
        return changed
            ? new GridReconstructionRequest(request.Surface, request.Lattice, cells, request.VerticalScrollPosition)
            : request;
    }

    private static EvidencedValue<T> Restamp<T>(EvidencedValue<T> field, EvidenceProvenance provenance) =>
        new(field.FieldId, field.Value, field.Status, provenance, field.Bounds);
}
