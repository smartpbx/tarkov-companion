namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>One plan-owned item suggestion projected into the shared Intel empty state.</summary>
public sealed record V2PlannedItemSuggestion
{
    public V2PlannedItemSuggestion(
        string itemId,
        string displayName,
        string planLabel,
        string objectiveLabel)
    {
        if (!V2AddressCodec.IsValidItem(itemId))
        {
            throw new ArgumentException("A planned suggestion must name an addressable item.", nameof(itemId));
        }

        ItemId = itemId;
        DisplayName = Bounded(displayName, nameof(displayName));
        PlanLabel = Bounded(planLabel, nameof(planLabel));
        ObjectiveLabel = Bounded(objectiveLabel, nameof(objectiveLabel), 256);
    }

    public string ItemId { get; }
    public string DisplayName { get; }
    public string PlanLabel { get; }
    public string ObjectiveLabel { get; }

    private static string Bounded(string value, string parameterName, int maximumLength = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Suggestion text must be bounded and contain no control characters.", parameterName);
        }

        return normalized;
    }
}
