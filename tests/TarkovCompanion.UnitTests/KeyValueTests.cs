using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Keep or sell, for keys.
/// </summary>
/// <remarks>
/// Built on Clayton's observation that the flea price of a key is a good indicator of what is
/// behind the door it opens — the market has already priced every key by its contents, and that
/// price is synced and updating, where the wiki's room contents are a scrape with a licence
/// problem attached.
///
/// The thing these tests are really guarding is the boundary between a signal and a guess. The
/// Keys page has never shown a tier, because the existing intelligence service weighs four
/// inputs that are projected as zero; nothing here may go the same way.
/// </remarks>
public sealed class KeyValueTests
{
    [Fact]
    public void A_quest_you_are_on_beats_every_other_signal()
    {
        // Selling a key a hand-in needs is the mistake the verdict exists to prevent, and it is
        // the one input here that is a fact about this player rather than an opinion about a key.
        var verdict = KeyValue.Judge(1_000, dearerThan: 0.01, lockCount: 0, maximumUses: 1, Needs(tracked: 1));

        Assert.Equal(KeepOrSell.Keep, verdict.Call);
        Assert.Contains("a quest you are on needs it", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quest you have not started yet is a weaker claim, and gets its own answer.
    /// </summary>
    /// <remarks>
    /// It was worded as the strong one and counted as neither. The page said "225 quests you
    /// are tracking ask for it" beside a dorm key on a profile tracking nothing, because the
    /// filter was "not completed" — on a fresh wipe, every quest in the game — and the number
    /// was a sum of outstanding item quantities rather than a count of quests.
    /// </remarks>
    [Fact]
    public void A_quest_ahead_of_you_is_a_weaker_keep_and_says_so()
    {
        var verdict = KeyValue.Judge(1_000, dearerThan: 0.01, lockCount: 0, maximumUses: 1, Needs(ahead: 4));

        Assert.Equal(KeepOrSell.KeepForLater, verdict.Call);
        Assert.Contains("4 quests ahead of you need it", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quest_you_are_on_outranks_one_that_is_merely_ahead()
    {
        // Both are true of the same key, and only the stronger is worth the row.
        var verdict = KeyValue.Judge(1_000, dearerThan: 0.9, lockCount: 0, maximumUses: 1, Needs(tracked: 1, ahead: 9));

        Assert.Equal(KeepOrSell.Keep, verdict.Call);
        Assert.DoesNotContain("ahead", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// One quest wanting two of something is one quest, not two.
    /// </summary>
    /// <remarks>
    /// The counts are counts now and the quantities are their own fields, which is the half of
    /// this that was wrong even when the number was small.
    /// </remarks>
    [Fact]
    public void Quantities_are_not_counted_as_quests()
    {
        var needs = new ItemNeedSummary(OutstandingItems: 7, OutstandingFoundInRaidItems: 0, HideoutCount: 0)
        {
            QuestsNeedingIt = 1,
            TrackedQuestsNeedingIt = 1,
        };

        Assert.Contains("a quest you are on needs it", KeyValue.Judge(1_000, 0.5, 0, null, needs).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hideout_build_keeps_a_key_the_market_would_sell()
    {
        var verdict = KeyValue.Judge(1_000, dearerThan: 0.02, lockCount: 1, maximumUses: null, Needs(hideout: 1));

        Assert.Equal(KeepOrSell.Keep, verdict.Call);
        Assert.Contains("hideout", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dear_key_is_kept()
    {
        Assert.Equal(KeepOrSell.Keep, KeyValue.Judge(600_000, 0.9, 3, null, Needs()).Call);
    }

    [Fact]
    public void A_cheap_key_is_sold()
    {
        Assert.Equal(KeepOrSell.Sell, KeyValue.Judge(3_000, 0.05, 1, null, Needs()).Call);
    }

    [Fact]
    public void The_middle_gets_no_call_at_all()
    {
        // The point of having a third answer. A verdict on every key would be a verdict that
        // means nothing on the ones where the market is undecided too.
        var verdict = KeyValue.Judge(40_000, 0.5, 2, null, Needs());

        Assert.Equal(KeepOrSell.NoCall, verdict.Call);
        Assert.Contains("your call", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpriced_key_is_told_it_cannot_be_ranked()
    {
        // Rather than sold. On a fresh install nothing has a price yet, and a verdict that read
        // "sell" on every key in the game would be worse than no page at all.
        var verdict = KeyValue.Judge(0, dearerThan: null, lockCount: 4, maximumUses: null, Needs());

        Assert.Equal(KeepOrSell.NoCall, verdict.Call);
        Assert.Contains("no price is cached", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_use_key_the_market_rates_is_still_kept_and_said_to_open_once()
    {
        // The use limit qualifies the market rather than overturning it.
        var verdict = KeyValue.Judge(900_000, 0.95, 1, maximumUses: 1, Needs());

        Assert.Equal(KeepOrSell.Keep, verdict.Call);
        Assert.Contains("opens once", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cheap_key_that_opens_nothing_cached_says_so()
    {
        var verdict = KeyValue.Judge(2_000, 0.02, lockCount: 0, maximumUses: null, Needs());

        Assert.Equal(KeepOrSell.Sell, verdict.Call);
        Assert.Contains("no cached lock", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_tracking_progress_still_leaves_the_market_to_answer()
    {
        // Null needs is "we do not know", not "no quest wants it". It must not read as the
        // second, or a client with quest tracking switched off would sell its quest keys.
        Assert.Equal(KeepOrSell.Keep, KeyValue.Judge(800_000, 0.99, 2, null, needs: null).Call);
        Assert.Equal(KeepOrSell.Sell, KeyValue.Judge(2_000, 0.01, 2, null, needs: null).Call);
    }

    [Fact]
    public void Ranking_puts_the_cheapest_at_the_bottom_and_the_dearest_at_the_top()
    {
        var ranks = KeyValue.Rank(Market(("cheap", 1_000), ("middle", 50_000), ("dear", 900_000)));

        Assert.Equal(0, ranks["cheap"], 6);
        Assert.Equal(1, ranks["dear"], 6);
        Assert.InRange(ranks["middle"], 0, 1);
    }

    [Fact]
    public void An_unpriced_key_is_not_ranked_at_the_bottom()
    {
        // Treating "unknown" as "worthless" would sell every key the sync has not priced, which
        // on a fresh install is all of them.
        var ranks = KeyValue.Rank(Market(("unpriced", 0), ("priced", 1_000)));

        Assert.False(ranks.ContainsKey("unpriced"));
        Assert.True(ranks.ContainsKey("priced"));
    }

    [Fact]
    public void Two_keys_at_the_same_price_cannot_get_opposite_verdicts()
    {
        var ranks = KeyValue.Rank(Market(("a", 40_000), ("b", 40_000)));

        Assert.Equal(ranks["a"], ranks["b"], 6);
    }

    [Fact]
    public void A_handful_of_priced_keys_is_not_a_market()
    {
        // One key is not a market and neither is three. Ranking below that would declare the
        // cheapest of four a sell on the strength of nothing, which is the state a fresh
        // install is in for every key it has.
        Assert.Empty(KeyValue.Rank([("only", 500_000)]));
        Assert.Empty(KeyValue.Rank([("a", 1), ("b", 2), ("c", 3)]));
    }

    [Fact]
    public void A_priced_key_that_could_not_be_ranked_is_told_which_silence_it_is()
    {
        // Two different "cannot say"s, and telling somebody the wrong reason is how they stop
        // believing the right ones. This key has a price; what it lacks is anything to compare
        // against.
        var verdict = KeyValue.Judge(500_000, dearerThan: null, lockCount: 2, maximumUses: null, Needs());

        Assert.Equal(KeepOrSell.NoCall, verdict.Call);
        Assert.Contains("too few keys have cached prices", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ranking_nothing_is_not_a_failure()
    {
        Assert.Empty(KeyValue.Rank([]));
        Assert.Empty(KeyValue.Rank([("unpriced", 0)]));
    }

    /// <summary>
    /// What the player's own progress asks for.
    /// </summary>
    /// <remarks>
    /// The quantities and the counts are separate arguments because they are separate facts:
    /// one quest can want three of something, and the page has to be able to say which number
    /// it means.
    /// </remarks>
    private static ItemNeedSummary Needs(int tracked = 0, int ahead = 0, int hideout = 0) =>
        new(OutstandingItems: tracked + ahead, OutstandingFoundInRaidItems: 0, HideoutCount: hideout)
        {
            // A quest you are on is also a quest that is ahead of you, so the wider count
            // includes it — which is what makes the ordering in Judge worth testing.
            QuestsNeedingIt = tracked + ahead,
            TrackedQuestsNeedingIt = tracked,
        };

    /// <summary>
    /// The named keys, padded out to a market large enough to rank.
    /// </summary>
    /// <remarks>
    /// Ranking needs a handful of priced keys before it means anything, and the padding is
    /// priced between the extremes so it cannot change which of the named ones is cheapest or
    /// dearest.
    /// </remarks>
    private static (string ItemId, long Roubles)[] Market(params (string ItemId, long Roubles)[] named) =>
    [
        .. named,
        .. Enumerable.Range(1, 8).Select(index => ($"filler-{index}", (long)(10_000 + (index * 1_000)))),
    ];
}
