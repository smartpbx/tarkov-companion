using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Sound;

/// <summary>The verb a spoken loot verdict starts with.</summary>
public enum LootVerdictWord
{
    None,
    Keep,
    SellFlea,
    SellTrader,
    Leave,
    Use,
    DoNotUse,
}

/// <summary>What a finished scan says, before it is put into words: "Keep: LEDX, 1.2 million".</summary>
/// <param name="Name">The item's short name for one item; null for a container.</param>
/// <param name="ItemCount">How many items a container scan found; 1 for one item.</param>
/// <param name="Value">Roubles, where a price was known.</param>
public sealed record LootVerdictLine(LootVerdictWord Word, string? Name, int ItemCount, long? Value)
{
    /// <summary>
    /// The line for a finished scan, or null when there is nothing worth saying: a screen that is not
    /// loot, a scan that failed, or an item that was not recognised. An unsure scan is never spoken
    /// as a verdict; the Loot page shows "not sure" and the ear hears nothing.
    /// </summary>
    public static LootVerdictLine? From(ScanOutcome scan, string? shortName = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Status == ScanCompletionStatus.Unavailable)
        {
            return null;
        }

        switch (scan.Context)
        {
            case ScanContext.SingleItem when scan.Recognition.Selected is { } item:
                return new(
                    WordFor(scan.Recommendation?.Action),
                    string.IsNullOrWhiteSpace(shortName) ? item.DisplayName : shortName.Trim(),
                    1,
                    Positive(scan.EconomicValue ?? scan.Recommendation?.SelectedEconomicValue));
            case ScanContext.Container when scan.Container is { Items.Count: > 0 } container:
                return new(LootVerdictWord.None, null, container.Items.Count, Positive(container.ApproximateValue));
            default:
                return null;
        }
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static LootVerdictWord WordFor(RecommendationAction? action) => action switch
    {
        RecommendationAction.EssentialKeep or RecommendationAction.Keep => LootVerdictWord.Keep,
        RecommendationAction.SellFlea => LootVerdictWord.SellFlea,
        RecommendationAction.SellTrader => LootVerdictWord.SellTrader,
        RecommendationAction.DropFirst => LootVerdictWord.Leave,
        RecommendationAction.Use => LootVerdictWord.Use,
        RecommendationAction.AvoidConsume => LootVerdictWord.DoNotUse,
        _ => LootVerdictWord.None,
    };
}

/// <summary>The words sound speaks, supplied by the app's string tables.</summary>
public interface ISoundLines
{
    string LootVerdict(LootVerdictLine line);

    string Test { get; }
}
