namespace TarkovCompanion.App.Services.TestChecklist;

/// <summary>What the tester has to hand, as a second filter beside the status one.</summary>
public enum TestChecklistNeedFilter
{
    Any,

    /// <summary>Nothing that needs the game running or a game screenshot: a desk session.</summary>
    NoGame,

    /// <summary>Needs a squadmate or the tablet page.</summary>
    SquadOrTablet,
}

/// <summary>The needs filter and the search box, kept apart from the page so they can be tested alone.</summary>
/// <remarks>
/// "No game needed" is decided by what an item names, not by its area: 390 of the 559 shipped items
/// can be walked through with the game closed, and they are spread over every area.
/// </remarks>
public static class TestChecklistNarrowing
{
    /// <summary>Needs that mean the game has to be running, or a picture it took.</summary>
    public static IReadOnlySet<string> GameNeeds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "game", "in-raid", "pmc", "scav", "stash-screenshot", "loot-screenshot", "flea-screenshot",
    };

    public static bool Passes(TestChecklistItem item, TestChecklistNeedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(item);
        return filter switch
        {
            TestChecklistNeedFilter.NoGame => !item.Needs.Any(GameNeeds.Contains),
            TestChecklistNeedFilter.SquadOrTablet => item.Needs.Any(need => need is "squad" or "tablet"),
            _ => true,
        };
    }

    /// <summary>Every word typed appears in the feature, a step, the expectation or the id, in any case.</summary>
    public static bool Matches(TestChecklistItem item, string? search)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(word =>
            Contains(item.Feature, word) ||
            Contains(item.Id, word) ||
            Contains(item.Expect, word) ||
            item.Steps.Any(step => Contains(step, word)));
    }

    private static bool Contains(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
