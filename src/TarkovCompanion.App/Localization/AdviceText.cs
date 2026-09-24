using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// The sentences the recommendation engine and the loot-scan planner give (#314). Each record keeps
/// its fixed English for storage and the relay, and carries the same sentence as a phrase when it
/// was produced on this machine; a record read back from storage has no phrase and shows its English.
/// </summary>
/// <remarks>
/// Its own class rather than part of <see cref="IntelText"/>: every argument here is a whole
/// engine record, which the table test that calls each IntelText accessor cannot make up.
/// </remarks>
public static class AdviceText
{
    /// <summary>Why the engine advised what it did, in the interface language.</summary>
    public static string Reason(RecommendationReason reason) => Said(reason.Words, reason.Explanation);

    /// <summary>A fact that would change the answer, in the interface language.</summary>
    public static string Reason(RecommendationSensitivity sensitivity) => Said(sensitivity.Words, sensitivity.Explanation);

    /// <summary>"Keep: …", the decision's one-line summary, in the interface language.</summary>
    public static string Summary(RecommendationDecision decision) => Said(decision.SummaryWords, decision.Summary);

    /// <summary>Why the loot-scan planner gave a cell its verdict, in the interface language.</summary>
    public static string Reason(LootScanReason reason) => Said(reason.Words, reason.Explanation);

    /// <summary>A gap in the whole loot scan, in the interface language.</summary>
    public static string Reason(LootScanIssue issue) => Said(issue.Words, issue.Explanation);

    private static string Said(Phrase? words, string english) => words is { } phrase ? PhraseText.Say(phrase) : english;
}
