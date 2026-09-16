using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Domain.Inventory;

/// <summary>The profile boundary that makes an observed holding safe to subtract from a need.</summary>
public sealed record InventoryProfileScope
{
    public InventoryProfileScope(Guid profileId, string generation, string gameMode)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile id is required.", nameof(profileId));
        }

        ProfileId = profileId;
        Generation = V2ContractGuard.Required(generation, nameof(generation));
        GameMode = V2ContractGuard.Required(gameMode, nameof(gameMode));
    }

    public Guid ProfileId { get; }

    public string Generation { get; }

    public string GameMode { get; }
}

/// <summary>
/// A positive observation of one item. An unread count remains absent; it is never represented as
/// zero. Found-in-raid quantity is separate because only that subset can satisfy a FIR objective.
/// </summary>
public sealed record ObservedItemCount
{
    public ObservedItemCount(
        string itemId,
        EvidencedValue<int?> totalQuantity,
        EvidencedValue<int?> foundInRaidQuantity)
    {
        ItemId = V2ContractGuard.Required(itemId, nameof(itemId));
        TotalQuantity = V2ContractGuard.AtLeast(totalQuantity, 0, nameof(totalQuantity));
        FoundInRaidQuantity = V2ContractGuard.AtLeast(foundInRaidQuantity, 0, nameof(foundInRaidQuantity));

        if (TotalQuantity.Value is { } total && FoundInRaidQuantity.Value is { } foundInRaid && foundInRaid > total)
        {
            throw new ArgumentException("Found-in-raid quantity cannot exceed total quantity.", nameof(foundInRaidQuantity));
        }
    }

    public string ItemId { get; }

    public EvidencedValue<int?> TotalQuantity { get; }

    public EvidencedValue<int?> FoundInRaidQuantity { get; }
}

/// <summary>
/// The immutable evidence used for holdings subtraction. Coverage describes the scanned surface,
/// while each count proves only what was positively observed on that surface.
/// </summary>
public sealed record ObservedInventoryEvidenceSnapshot
{
    public const int MaximumItemKinds = 4096;

    private readonly IReadOnlyDictionary<string, ObservedItemCount> _byItemId;

    public ObservedInventoryEvidenceSnapshot(
        Guid snapshotId,
        InventoryProfileScope scope,
        string dataSnapshotId,
        ResultStatus status,
        EvidenceCoverage coverage,
        EvidenceProvenance provenance,
        IReadOnlyList<ObservedItemCount> items,
        int? unresolvedCells = null)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot id is required.", nameof(snapshotId));
        }

        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(items);
        if (unresolvedCells is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unresolvedCells));
        }

        var copied = items
            .Select(item => item ?? throw new ArgumentException("Observed items cannot contain null.", nameof(items)))
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > MaximumItemKinds)
        {
            throw new ArgumentException($"An inventory snapshot cannot exceed {MaximumItemKinds} item kinds.", nameof(items));
        }

        if (copied.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Observed item identifiers must be unique.", nameof(items));
        }

        SnapshotId = snapshotId;
        Scope = scope;
        DataSnapshotId = V2ContractGuard.Required(dataSnapshotId, nameof(dataSnapshotId));
        Status = status;
        Coverage = coverage;
        Provenance = provenance;
        Items = Array.AsReadOnly(copied);
        UnresolvedCells = unresolvedCells;
        _byItemId = copied.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
    }

    public Guid SnapshotId { get; }

    public InventoryProfileScope Scope { get; }

    public string DataSnapshotId { get; }

    public ResultStatus Status { get; }

    public EvidenceCoverage Coverage { get; }

    public EvidenceProvenance Provenance { get; }

    public IReadOnlyList<ObservedItemCount> Items { get; }

    public int? UnresolvedCells { get; }

    public ObservedItemCount? Find(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return _byItemId.GetValueOrDefault(itemId.Trim());
    }
}

public interface IObservedInventoryEvidenceReader
{
    Task<ObservedInventoryEvidenceSnapshot?> ReadCurrentAsync(
        InventoryProfileScope scope,
        CancellationToken cancellationToken);
}
