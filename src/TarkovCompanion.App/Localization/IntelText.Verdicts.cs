using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// Verdict words the Application layer decides but does not say (#314): a key's keep-or-sell
/// reason, and the one-word recommendation beside an item.
/// </summary>
public static partial class IntelText
{
    /// <summary>"a quest you are on needs it", "dearer than four keys in five of priced keys"…</summary>
    public static string KeyReason(KeyReasonCode? code)
    {
        if (code is null)
        {
            return string.Empty;
        }

        return code.Reason switch
        {
            KeyVerdictReason.TrackedQuestsNeedIt => UiText.Plural("Intel.KeyReason.TrackedQuests", code.Count),
            KeyVerdictReason.QuestsAheadNeedIt => UiText.Plural("Intel.KeyReason.QuestsAhead", code.Count),
            KeyVerdictReason.HideoutNeedsIt => UiText.Get("Intel.KeyReason.Hideout"),
            KeyVerdictReason.NoPrice => UiText.Get("Intel.KeyReason.NoPrice"),
            KeyVerdictReason.TooFewPriced => UiText.Get("Intel.KeyReason.TooFewPriced"),
            KeyVerdictReason.Dearer => UiText.Format("Intel.KeyReason.Dearer", KeyShare(code.Share)),
            KeyVerdictReason.DearerOpensOnce => UiText.Format("Intel.KeyReason.DearerOpensOnce", KeyShare(code.Share)),
            KeyVerdictReason.Cheaper => UiText.Format("Intel.KeyReason.Cheaper", KeyShare(code.Share)),
            KeyVerdictReason.CheaperNoLock => UiText.Format("Intel.KeyReason.CheaperNoLock", KeyShare(code.Share)),
            KeyVerdictReason.Middle => UiText.Get("Intel.KeyReason.Middle"),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code.Reason, "No words for this key reason."),
        };
    }

    public static string KeyShare(KeyShareBand band) => band switch
    {
        KeyShareBand.NearlyEvery => UiText.Get("Intel.KeyShare.NearlyEvery"),
        KeyShareBand.NineInTen => UiText.Get("Intel.KeyShare.NineInTen"),
        KeyShareBand.FourInFive => UiText.Get("Intel.KeyShare.FourInFive"),
        KeyShareBand.ThreeInFour => UiText.Get("Intel.KeyShare.ThreeInFour"),
        KeyShareBand.TwoInThree => UiText.Get("Intel.KeyShare.TwoInThree"),
        KeyShareBand.Half => UiText.Get("Intel.KeyShare.Half"),
        KeyShareBand.Third => UiText.Get("Intel.KeyShare.Third"),
        KeyShareBand.Fifth => UiText.Get("Intel.KeyShare.Fifth"),
        _ => UiText.Get("Intel.KeyShare.AlmostNone"),
    };

    /// <summary>The one word beside an item: Keep, Sell, Use soon, Don't use, Take, Leave or Review.</summary>
    public static string RecommendationVerdict(RecommendationAction action) => action switch
    {
        RecommendationAction.Keep => UiText.Get("Intel.Recommend.Keep"),
        RecommendationAction.SellOnFlea or RecommendationAction.SellToTrader => UiText.Get("Intel.Recommend.Sell"),
        RecommendationAction.UseSoon => UiText.Get("Intel.Recommend.UseSoon"),
        RecommendationAction.AvoidConsume => UiText.Get("Intel.Recommend.DontUse"),
        RecommendationAction.Take => UiText.Get("Intel.Recommend.Take"),
        RecommendationAction.Leave => UiText.Get("Intel.Recommend.Leave"),
        _ => UiText.Get("Intel.Recommend.Review"),
    };
}
