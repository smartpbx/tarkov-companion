using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Keys;

namespace TarkovCompanion.Application.Services.Intelligence.Keys;

public enum KeyInventoryCorrectionTarget
{
    TotalOwned = 1,
    FoundInRaidOwned,
    DuplicateQuantity,
    MaximumUses,
    RemainingUses,
}

/// <summary>A profile-bound review intent. Applying it never changes EFT inventory.</summary>
public sealed record KeyInventoryCorrectionCommand
{
    public KeyInventoryCorrectionCommand(
        Guid commandId,
        InventoryProfileScope profileScope,
        string itemId,
        KeyInventoryCorrectionTarget target,
        int? expectedValue,
        int correctedValue,
        DateTimeOffset correctedUtc,
        CorrectionOriginClass originClass,
        string originIdentifier,
        string? reason = null)
    {
        CommandId = commandId != Guid.Empty ? commandId : throw new ArgumentException("A command id is required.", nameof(commandId));
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ItemId = itemId.Trim();
        if (ItemId.Length > KeyIntelligenceBounds.MaximumIdentifierLength)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId));
        }

        Target = Enum.IsDefined(target) ? target : throw new ArgumentOutOfRangeException(nameof(target));
        ExpectedValue = expectedValue;
        CorrectedValue = correctedValue >= 0 ? correctedValue : throw new ArgumentOutOfRangeException(nameof(correctedValue));
        CorrectedUtc = correctedUtc != default && correctedUtc.Offset == TimeSpan.Zero
            ? correctedUtc
            : throw new ArgumentException("Correction time must be a defined UTC instant.", nameof(correctedUtc));
        OriginClass = Enum.IsDefined(originClass) ? originClass : throw new ArgumentOutOfRangeException(nameof(originClass));
        ArgumentException.ThrowIfNullOrWhiteSpace(originIdentifier);
        OriginIdentifier = originIdentifier.Trim();
        if (OriginIdentifier.Length > KeyIntelligenceBounds.MaximumIdentifierLength)
        {
            throw new ArgumentOutOfRangeException(nameof(originIdentifier));
        }

        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (Reason?.Length > KeyIntelligenceBounds.MaximumExplanationLength)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    public Guid CommandId { get; }
    public InventoryProfileScope ProfileScope { get; }
    public string ItemId { get; }
    public KeyInventoryCorrectionTarget Target { get; }
    public int? ExpectedValue { get; }
    public int CorrectedValue { get; }
    public DateTimeOffset CorrectedUtc { get; }
    public CorrectionOriginClass OriginClass { get; }
    public string OriginIdentifier { get; }
    public string? Reason { get; }
}

/// <summary>
/// Appends attributable scalar corrections and reverses them by appending a compensating entry;
/// the recognized value and every intervening correction remain available for review.
/// </summary>
public sealed class KeyInventoryCorrectionService
{
    public KeyInventoryFacts Apply(KeyInventoryFacts current, KeyInventoryCorrectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(current, command.ProfileScope, command.ItemId);
        var field = Field(current, command.Target);
        if (field.Value != command.ExpectedValue)
        {
            throw new InvalidOperationException("The key fact changed after this correction was prepared.");
        }

        return Replace(current, command.Target, Append(
            field,
            command.CorrectedValue,
            command.CorrectedUtc,
            command.OriginClass,
            command.OriginIdentifier,
            command.Reason));
    }

    public KeyInventoryFacts ReverseLast(
        KeyInventoryFacts current,
        InventoryProfileScope profileScope,
        string itemId,
        KeyInventoryCorrectionTarget target,
        DateTimeOffset correctedUtc,
        CorrectionOriginClass originClass,
        string originIdentifier,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateScope(current, profileScope, itemId);
        var field = Field(current, target);
        if (field.Corrections.Count == 0)
        {
            throw new InvalidOperationException("The selected key fact has no correction to reverse.");
        }

        var prior = field.Corrections[^1].OriginalValue;
        if (prior is null)
        {
            throw new InvalidOperationException(
                "Reversing to an unresolved raw value requires an explicit review decision instead of manufacturing a number.");
        }

        return Replace(current, target, Append(
            field,
            prior.Value,
            correctedUtc,
            originClass,
            originIdentifier,
            reason ?? "Reversed the preceding key fact correction."));
    }

    private static EvidencedValue<int?> Append(
        EvidencedValue<int?> field,
        int correctedValue,
        DateTimeOffset correctedUtc,
        CorrectionOriginClass originClass,
        string originIdentifier,
        string? reason)
    {
        if (field.Corrections.Count >= KeyIntelligenceBounds.MaximumCorrectionsPerField)
        {
            throw new InvalidOperationException("The bounded key correction history is full.");
        }

        var corrections = field.Corrections
            .Append(new EvidenceCorrection<int?>(
                field.Corrections.Count + 1L,
                field.Value,
                correctedValue,
                correctedUtc,
                originClass,
                originIdentifier,
                reason))
            .ToArray();
        return new EvidencedValue<int?>(
            field.FieldId,
            correctedValue,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "key.correction.applied"),
            field.Provenance,
            field.Bounds,
            field.Candidates,
            corrections);
    }

    private static EvidencedValue<int?> Field(KeyInventoryFacts current, KeyInventoryCorrectionTarget target) => target switch
    {
        KeyInventoryCorrectionTarget.TotalOwned => current.TotalOwned,
        KeyInventoryCorrectionTarget.FoundInRaidOwned => current.FoundInRaidOwned,
        KeyInventoryCorrectionTarget.DuplicateQuantity => current.DuplicateQuantity,
        KeyInventoryCorrectionTarget.MaximumUses => current.MaximumUses,
        KeyInventoryCorrectionTarget.RemainingUses => current.RemainingUses,
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static KeyInventoryFacts Replace(
        KeyInventoryFacts current,
        KeyInventoryCorrectionTarget target,
        EvidencedValue<int?> replacement) =>
        new(
            current.ProfileScope,
            current.ItemId,
            target == KeyInventoryCorrectionTarget.TotalOwned ? replacement : current.TotalOwned,
            target == KeyInventoryCorrectionTarget.FoundInRaidOwned ? replacement : current.FoundInRaidOwned,
            target == KeyInventoryCorrectionTarget.DuplicateQuantity ? replacement : current.DuplicateQuantity,
            target == KeyInventoryCorrectionTarget.MaximumUses ? replacement : current.MaximumUses,
            target == KeyInventoryCorrectionTarget.RemainingUses ? replacement : current.RemainingUses);

    private static void ValidateScope(KeyInventoryFacts current, InventoryProfileScope scope, string itemId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (current.ProfileScope != scope || !string.Equals(current.ItemId, itemId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A key correction cannot cross its profile, generation, mode, or item scope.");
        }
    }
}
