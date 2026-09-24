using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.UnitTests.Localization;

/// <summary>
/// Checks that every sentence of a recommendation or loot scan carries its words, and that the App
/// saying those words in English reproduces the stored English byte for byte (#314).
/// </summary>
/// <remarks>
/// The engine and planner tests run every result they build through this, so the check covers
/// every input those tests construct rather than a hand-picked sample: a sentence the engine gains
/// without a phrase, or a table entry that drifts from the old English, fails where it is produced.
/// </remarks>
internal static class AdviceWordsCheck
{
    private static readonly UiStrings English = UiText.Create("en", _ => { });

    public static RecommendationResult SaidAsBefore(RecommendationResult result)
    {
        InEnglish(() => Check(result));
        return result;
    }

    public static LootScanResult SaidAsBefore(LootScanResult result)
    {
        InEnglish(() =>
        {
            foreach (var issue in result.Issues)
            {
                Same(issue.Code, issue.Words, issue.Explanation);
            }

            foreach (var decision in result.Decisions)
            {
                foreach (var reason in decision.Reasons)
                {
                    Same(reason.Code, reason.Words, reason.Explanation);
                }

                if (decision.Recommendation is { } recommendation)
                {
                    Check(recommendation);
                }
            }
        });
        return result;
    }

    private static void Check(RecommendationResult result)
    {
        if (result.Decision.Value is not { } decision)
        {
            return;
        }

        foreach (var reason in decision.Reasons)
        {
            Same(reason.Code, reason.Words, reason.Explanation);
        }

        foreach (var sensitivity in decision.ChangesTheAnswer)
        {
            Same(sensitivity.FactCode, sensitivity.Words, sensitivity.Explanation);
        }

        Same("summary", decision.SummaryWords, decision.Summary);
    }

    private static void Same(string what, Phrase? words, string english)
    {
        Assert.True(words is not null, $"{what} has no words beside its English: {english}");
        Assert.Equal(english, PhraseText.Say(words!));
    }

    private static void InEnglish(Action check)
    {
        using var scope = UiText.Scope(English);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            check();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
