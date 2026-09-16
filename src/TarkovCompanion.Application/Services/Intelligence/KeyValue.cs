using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.Intelligence;

/// <summary>What to do with a key you just picked up.</summary>
public enum KeepOrSell
{
    /// <summary>Nothing here is a strong enough signal to say, and it says so.</summary>
    NoCall,

    /// <summary>A quest the player is on needs it. The strongest thing this can say.</summary>
    Keep,

    /// <summary>
    /// A quest still ahead of them needs it.
    /// </summary>
    /// <remarks>
    /// Its own answer rather than a Keep with different words, because the two are read at
    /// different moments: one is "do not sell this today" and the other is "this will matter
    /// eventually, and on a fresh wipe so will almost everything". Collapsing them into Keep is
    /// how the page came to mark two hundred and thirty-six keys of two hundred and fifty-seven
    /// as Keep and mean nothing by it.
    /// </remarks>
    KeepForLater,
    Sell,
}

/// <summary>A verdict on one key, and the one fact that decided it.</summary>
/// <param name="Call">Keep, sell, or neither.</param>
/// <param name="Reason">Why, naming the fact rather than a score.</param>
public sealed record KeyVerdict(KeepOrSell Call, string Reason);

/// <summary>
/// Keep or sell, for keys, from what the synced data actually states.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>KeyIntelligenceService</c>. That weighs six inputs and four of them —
/// expected loot, lock utility, unique access and route risk — have no source anywhere in the
/// synced payload and are projected as zero, which is why the Keys page has never used it: every
/// key would land in the same band for a reason that has nothing to do with the key. Nothing
/// here is computed from a zero.
/// </para>
/// <para>
/// The insight this is built on is Clayton's: <i>"the flea price of a key is a good indicator of
/// its loot and keep value too"</i>. It is right, and it removes a dependency. The market has
/// already priced every key by what is behind the door it opens, thousands of players at a time,
/// and that price is synced and updating. The wiki's room contents would be nicer and are a
/// scrape with a licence and a fragility problem attached; this is a free proxy that is already
/// here.
/// </para>
/// <para>
/// Ranked against the other keys rather than against a rouble figure. A threshold in roubles
/// would be a number somebody made up, and it would rot as the market moved; "dearer than four
/// keys in five" is a statement about the data in front of it and stays true as prices change.
/// </para>
/// <para>
/// The middle half gets no call, which is the point of having a third answer. A verdict on every
/// key would be a verdict that means nothing on the ones where the market is undecided too.
/// </para>
/// </remarks>
public static class KeyValue
{
    /// <summary>Above this share of other keys, the market is saying the door is worth it.</summary>
    private const double Dear = 0.75;

    /// <summary>Below this, it is saying the opposite.</summary>
    private const double Cheap = 0.25;

    /// <summary>
    /// How many priced keys it takes before ranking one against the others means anything.
    /// </summary>
    /// <remarks>
    /// One key is not a market, and neither is three. Below this nothing is ranked at all, so a
    /// sync that has priced a handful of keys says it cannot tell rather than declaring the
    /// cheapest of four a sell. This is the state a fresh install is in.
    /// </remarks>
    private const int MinimumToRank = 8;

    /// <summary>
    /// What to do with one key.
    /// </summary>
    /// <param name="roubles">What the market is paying, or null where nothing is cached.</param>
    /// <param name="dearerThan">
    /// The share of priced keys this one is dearer than, or null where it could not be ranked.
    /// </param>
    /// <param name="lockCount">How many locks the projection says it opens.</param>
    /// <param name="maximumUses">Its use limit, where the source states one.</param>
    /// <param name="needs">What the player's own tracked progress asks for.</param>
    public static KeyVerdict Judge(
        long? roubles,
        double? dearerThan,
        int lockCount,
        int? maximumUses,
        ItemNeedSummary? needs)
    {
        // Your own progress first, and it is not close. Every other signal here is the market's
        // opinion about a key in general; this one is a fact about the quest you are on, and
        // selling a key a hand-in needs is the mistake this whole verdict exists to prevent.
        if (needs is { TrackedQuestsNeedingIt: > 0 })
        {
            return new(KeepOrSell.Keep, needs.TrackedQuestsNeedingIt == 1
                ? "a quest you are on needs it"
                : $"{needs.TrackedQuestsNeedingIt} quests you are on need it");
        }

        // Then the quests that are still ahead of you, which is a weaker claim and said as one.
        // On a fresh wipe that is every quest in the game, so it cannot carry the same weight as
        // the one above it — but a key for a quest you have not started yet is still a key you
        // will want, and calling it a sell is the same mistake one step later.
        //
        // This used to be the only branch, and it was worded as the one above: "225 quests you
        // are tracking ask for it", beside a dorm key, on a profile tracking nothing.
        if (needs is { QuestsNeedingIt: > 0 })
        {
            return new(KeepOrSell.KeepForLater, needs.QuestsNeedingIt == 1
                ? "a quest ahead of you needs it"
                : $"{needs.QuestsNeedingIt} quests ahead of you need it");
        }

        if (needs is { HideoutCount: > 0 })
        {
            return new(KeepOrSell.Keep, "a hideout build asks for it");
        }

        // Two different silences, said differently. A key nothing has priced and a key that
        // could not be ranked because too few of the others are priced are both "cannot say",
        // and telling somebody the wrong reason is how they stop believing the right ones.
        if (roubles is not > 0)
        {
            return new(KeepOrSell.NoCall, "no price is cached, so nothing here can rank it");
        }

        if (dearerThan is not { } rank)
        {
            return new(KeepOrSell.NoCall, "too few keys have cached prices to rank this one against");
        }

        var among = $"dearer than {Share(rank)} of priced keys";
        if (rank >= Dear)
        {
            // The use limit does not overturn the market; it qualifies it. A one-use key the
            // market prices highly is still worth carrying, once.
            return new(KeepOrSell.Keep, maximumUses == 1
                ? $"{among}, and it opens once"
                : among);
        }

        if (rank <= Cheap)
        {
            // A key that opens nothing the projection knows about is the clearest sell there
            // is: cheap, and nothing cached says it goes anywhere.
            return new(KeepOrSell.Sell, lockCount == 0
                ? $"cheaper than {Share(1 - rank)} of priced keys, and no cached lock lists it"
                : $"cheaper than {Share(1 - rank)} of priced keys");
        }

        return new(KeepOrSell.NoCall, "the market prices it in the middle, so this is your call");
    }

    /// <summary>
    /// Ranks every priced key against the others, once.
    /// </summary>
    /// <remarks>
    /// A key with no cached price is not ranked at all rather than ranked at zero. Treating
    /// "unknown" as "worthless" would sell every key the sync has not priced yet, which on a
    /// fresh install is all of them.
    ///
    /// Ties share a rank, so two keys at the same price cannot get opposite verdicts.
    /// </remarks>
    public static IReadOnlyDictionary<string, double> Rank(IEnumerable<(string ItemId, long Roubles)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var priced = keys.Where(key => key.Roubles > 0).ToArray();
        if (priced.Length < MinimumToRank)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        // Against how many are strictly cheaper, so the cheapest is 0 and a key dearer than
        // everything else is 1. Strictly, so keys at the same price share a rank and two
        // identical keys cannot come out with opposite verdicts.
        var prices = priced.Select(key => key.Roubles).Order().ToArray();
        var ranked = new Dictionary<string, double>(priced.Length, StringComparer.Ordinal);
        foreach (var (itemId, roubles) in priced)
        {
            ranked[itemId] = (double)LowerBound(prices, roubles) / (prices.Length - 1);
        }

        return ranked;
    }

    /// <summary>How many entries of the sorted run are strictly below <paramref name="value"/>.</summary>
    private static int LowerBound(long[] sorted, long value)
    {
        var low = 0;
        var high = sorted.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// A share as a fraction somebody would say out loud.
    /// </summary>
    /// <remarks>
    /// "Four keys in five" rather than "the 81st percentile". The precision the second implies
    /// is not there: the input is a flea price that moves hourly and a key list that depends on
    /// what has synced.
    /// </remarks>
    private static string Share(double fraction) => fraction switch
    {
        >= 0.95 => "nearly every other key",
        >= 0.88 => "nine keys in ten",
        >= 0.78 => "four keys in five",
        >= 0.70 => "three keys in four",
        >= 0.60 => "two keys in three",
        >= 0.45 => "half the keys",
        >= 0.28 => "a third of the keys",
        >= 0.15 => "a fifth of the keys",
        _ => "almost no other key",
    };
}
