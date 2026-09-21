using TarkovCompanion.Application.Services.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Issue 508: a quest objective's marker is lettered, never numbered, so it can never collide
/// with a waypoint's route number.
/// </summary>
public sealed class QuestObjectiveLettersTests
{
    [Theory]
    [InlineData(1, "A")]
    [InlineData(2, "B")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(28, "AB")]
    [InlineData(52, "AZ")]
    [InlineData(53, "BA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void Letters_follow_the_spreadsheet_column_scheme(int index, string expected) =>
        Assert.Equal(expected, QuestObjectiveLetters.LetterFor(index));

    [Fact]
    public void The_order_never_repeats_a_letter_on_one_map()
    {
        // Customs, the largest real map, lists fewer than thirty active-quest objectives; run
        // well past both 26 and 702 (the second rollover) and every letter must still be unique.
        var letters = Enumerable.Range(1, 800).Select(QuestObjectiveLetters.LetterFor).ToArray();

        Assert.Equal(letters.Length, letters.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Letters_read_in_the_same_order_as_the_indices_that_made_them()
    {
        // A marker's letter must read as later than an earlier one's, the same way a waypoint's
        // "2" reads as later than its "1" — including across the 26-to-27 rollover to two
        // letters, where "Z" (index 26) is shorter than, and so sorts ordinally *after*, "AA"
        // (index 27) — the same non-lexicographic quirk a spreadsheet's own columns have. Length
        // first, then the letters themselves, is what "reads as later" actually means here.
        var letters = Enumerable.Range(1, 60).Select(QuestObjectiveLetters.LetterFor).ToArray();

        for (var index = 1; index < letters.Length; index++)
        {
            var previous = letters[index - 1];
            var current = letters[index];
            var readsLater = current.Length > previous.Length ||
                (current.Length == previous.Length && string.CompareOrdinal(previous, current) < 0);
            Assert.True(readsLater, $"{previous} did not read before {current}.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_index_below_one_is_refused(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QuestObjectiveLetters.LetterFor(index));

    [Theory]
    [InlineData("A", 1)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("AZ", 52)]
    [InlineData("BA", 53)]
    [InlineData("AAA", 703)]
    public void IndexFor_is_the_exact_inverse_of_LetterFor(string letters, int index) =>
        Assert.Equal(index, QuestObjectiveLetters.IndexFor(letters));

    [Theory]
    [InlineData(1)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(300)]
    [InlineData(701)]
    [InlineData(702)]
    [InlineData(703)]
    public void Every_letter_round_trips_through_its_own_index(int index) =>
        Assert.Equal(index, QuestObjectiveLetters.IndexFor(QuestObjectiveLetters.LetterFor(index)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("a")]
    [InlineData("Dorms")]
    // A "2" is a waypoint's own number, not something a letter sequence continues counting from.
    public void Anything_that_is_not_one_of_its_own_letters_counts_as_nothing_that_came_before(string? notALetter) =>
        Assert.Equal(0, QuestObjectiveLetters.IndexFor(notALetter));
}

/// <summary>
/// Issue 571: a raid's objective letters are decided once and kept, so removing the middle one —
/// by hand, or because the game reported its quest done — never turns C into B while the player
/// is looking at the map.
/// </summary>
public sealed class QuestObjectiveLetterAssignmentTests
{
    [Fact]
    public void The_same_objective_asked_twice_keeps_its_first_letter()
    {
        var letters = new QuestObjectiveLetterAssignment();

        var first = letters.LetterFor("objective-a");
        var second = letters.LetterFor("objective-a");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Removing_the_middle_one_does_not_relabel_the_ones_still_on_the_map()
    {
        var letters = new QuestObjectiveLetterAssignment();
        var a = letters.LetterFor("objective-a");
        var b = letters.LetterFor("objective-b");
        var c = letters.LetterFor("objective-c");

        // "objective-b" leaves the map (done, or its quest no longer active) and is never asked
        // for again this raid — the survivors are asked for again, as a rebuild would ask them.
        var aAgain = letters.LetterFor("objective-a");
        var cAgain = letters.LetterFor("objective-c");

        Assert.Equal(a, aAgain);
        Assert.Equal(c, cAgain);
        Assert.Equal("A", a);
        Assert.Equal("B", b);
        Assert.Equal("C", c);
    }

    [Fact]
    public void A_new_objective_gets_the_next_free_letter_not_a_removed_ones()
    {
        var letters = new QuestObjectiveLetterAssignment();
        letters.LetterFor("objective-a");
        letters.LetterFor("objective-b");
        letters.LetterFor("objective-c");

        // "objective-b" is gone; a genuinely new objective (a fourth quest picked up mid-raid)
        // must never be handed its now-unused letter, or two different objectives would have
        // been called "B" during the same raid.
        var added = letters.LetterFor("objective-d");

        Assert.Equal("D", added);
    }

    [Fact]
    public void More_than_twenty_six_objectives_roll_over_to_two_letters_like_the_scheme_itself()
    {
        var letters = new QuestObjectiveLetterAssignment();
        var assigned = Enumerable.Range(0, 30)
            .Select(index => letters.LetterFor($"objective-{index}"))
            .ToArray();

        Assert.Equal("Z", assigned[25]);
        Assert.Equal("AA", assigned[26]);
        Assert.Equal("AD", assigned[29]);
        Assert.Equal(assigned.Length, assigned.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Reset_starts_a_new_raids_alphabet_over()
    {
        var letters = new QuestObjectiveLetterAssignment();
        letters.LetterFor("objective-a");
        letters.LetterFor("objective-b");

        letters.Reset();

        // A different raid could easily place a wholly different objective first; what matters is
        // that the alphabet starts at "A" again rather than continuing from where the last raid
        // left off.
        Assert.Equal("A", letters.LetterFor("objective-z"));
    }
}
