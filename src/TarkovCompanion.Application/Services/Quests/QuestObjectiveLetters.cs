namespace TarkovCompanion.Application.Services.Quests;

/// <summary>
/// The letter a quest objective's marker and list row are called on one map: A, B, C, … Z, AA,
/// AB, … — the same bijective base-26 scheme a spreadsheet names its columns with.
/// </summary>
/// <remarks>
/// Issue 508: a quest objective used to be numbered 1, 2, 3 exactly like a waypoint, so a "2" on
/// the map could be either one. A number only ever means a waypoint's place in its route now; an
/// objective is lettered instead, so the two can never collide, however many objectives (or
/// waypoints) a map ends up carrying. Twenty-six single letters covers every real map — Customs,
/// the largest, lists fewer than thirty active-quest objectives at once — but the scheme itself is
/// unbounded, the same way a spreadsheet's columns keep going past Z.
/// </remarks>
public static class QuestObjectiveLetters
{
    /// <summary>
    /// 1 → "A", 26 → "Z", 27 → "AA", 28 → "AB", 52 → "AZ", 53 → "BA", …
    /// </summary>
    public static string LetterFor(int oneBasedIndex)
    {
        if (oneBasedIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBasedIndex), "A marker's letter is numbered from 1.");
        }

        // Bijective base-26: unlike ordinary base-26, there is no digit for zero, so the letters
        // never repeat a prefix (base-26 with a zero digit would call the 26th objective "Z" and
        // the 27th "10", i.e. "BA" written with A as zero — indistinguishable from the 53rd).
        var value = oneBasedIndex;
        var letters = new Stack<char>();
        while (value > 0)
        {
            value--;
            letters.Push((char)('A' + (value % 26)));
            value /= 26;
        }

        return new string(letters.ToArray());
    }

    /// <summary>
    /// The inverse of <see cref="LetterFor"/>: "A" → 1, "Z" → 26, "AA" → 27, … Zero for anything
    /// that is not one of <see cref="LetterFor"/>'s own outputs (blank, lower-case, a digit, a
    /// custom waypoint name) — the safe "nothing came before this" answer for a caller counting
    /// how far the sequence has already gone.
    /// </summary>
    public static int IndexFor(string? letters)
    {
        if (string.IsNullOrEmpty(letters))
        {
            return 0;
        }

        var value = 0;
        foreach (var character in letters)
        {
            if (character is < 'A' or > 'Z')
            {
                return 0;
            }

            value = (value * 26) + (character - 'A' + 1);
        }

        return value;
    }
}
