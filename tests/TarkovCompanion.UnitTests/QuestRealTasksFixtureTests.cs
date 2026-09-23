using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Hand-transcribed Task-column names from five consecutive real SIDE-tab screenfuls. No image,
/// path, account detail, location, status or progress value is retained in the repository.
/// </summary>
public sealed class QuestRealTasksFixtureTests
{
    private static readonly string[] StoryChapterTitles =
    [
        "Falling Skies", "Batya", "The Unheard", "Blue Fire", "They Are Already Here",
        "Accidental Witness", "Boreas",
    ];

    private static readonly string[] OperationalTaskNames =
    [
        "Elimination", "Exit the location", "Elimination",
    ];

    private static readonly string[][] Screenfuls =
    [
        [
            "The Punisher - Part 2", "Stimulating Demand", "From Hand to Hand", "Top Secret",
            "Pyramid Scheme", "To the Light - Trust but Verify", "Balancing - Part 1 [Season PvP]",
            "Seaside Vacation",
        ],
        [
            "Balancing - Part 1 [Season PvP]", "Seaside Vacation", "Car Repair", "Tarkov-Style Diplomacy",
            "Preliminary Survey", "The Door", "Corporate Secrets", "Gunsmith - AKS-74N", "Safe Corridor",
        ],
        [
            "The Door", "Corporate Secrets", "Gunsmith - AKS-74N", "Safe Corridor", "Easy-Breezy",
            "Documents", "Special Comms", "Irresistible", "Missing Cargo",
        ],
        [
            "Special Comms", "Irresistible", "Missing Cargo", "Lost Contact", "Charity", "Easy Job",
            "The Tarkov Butcher", "Job for a Patriot", "Delivery From the Past",
        ],
        [
            "The Tarkov Butcher", "Job for a Patriot", "Delivery From the Past", "Kings of the Rooftops",
            "The Hermit", "The Huntsman Path - Administrator", "Reserve", "Drip-Out - Part 1", "Booze",
        ],
    ];

    [Fact]
    public void FiveOverlappingScreenfulsBecomeOneQuestList()
    {
        var matcher = new QuestListMatcher();
        var merger = new QuestListMatchMerger();

        var merged = merger.Merge(Screenfuls.SelectMany(lines => matcher.Match(lines, Catalog()).Lines));

        Assert.Equal(32, merged.Lines.Count);
        Assert.Equal(29, merged.Matched.Count);
        Assert.Equal(29, merged.Matched.Select(line => line.Confirmed!.TaskId).Distinct().Count());
        Assert.Equal(
            ["Stimulating Demand", "To the Light - Trust but Verify", "Preliminary Survey"],
            merged.Unmatched.Select(line => line.OcrLine));
    }

    [Fact]
    public void SeasonalTagStillMatchesTheCatalogQuest()
    {
        var result = new QuestListMatcher().Match(
            ["Balancing - Part 1 [Season PvP]"],
            Catalog());

        Assert.Equal("Balancing - Part 1 [PVP ZONE]", Assert.Single(result.Matched).Confirmed!.QuestName);
    }

    [Fact]
    public void StoryChapterTitlesAbsentFromSeedCatalogRemainVisibleAndNeverGuess()
    {
        var result = new QuestListMatcher().Match(StoryChapterTitles, Catalog());

        Assert.Empty(result.Matched);
        Assert.Equal(StoryChapterTitles, result.Lines.Select(line => line.OcrLine));
    }

    [Fact]
    public void OperationalTasksAbsentFromSeedCatalogAreNotAppliedByNameAlone()
    {
        var matcher = new QuestListMatcher();
        var merger = new QuestListMatchMerger();

        var merged = merger.Merge(matcher.Match(OperationalTaskNames, Catalog()).Lines);

        Assert.Empty(merged.Matched);
        Assert.Equal(["Elimination", "Exit the location"], merged.Unmatched.Select(line => line.OcrLine));
    }

    private static QuestTaskDefinition[] Catalog() =>
    [
        Quest("Balancing - Part 1 [PVP ZONE]"), Quest("Booze"), Quest("Car Repair"), Quest("Charity"),
        Quest("Corporate Secrets"), Quest("Delivery From the Past"), Quest("Documents"),
        Quest("Drip-Out - Part 1"), Quest("Easy Job"), Quest("Easy-Breezy"), Quest("From Hand to Hand"),
        Quest("Gunsmith - AKS-74N"), Quest("Irresistible"), Quest("Job for a Patriot"),
        Quest("Kings of the Rooftops"), Quest("Lost Contact"), Quest("Missing Cargo"), Quest("Pyramid Scheme"),
        Quest("Reserve"), Quest("Safe Corridor"), Quest("Seaside Vacation"), Quest("Special Comms"),
        Quest("Tarkov-Style Diplomacy"), Quest("The Door"), Quest("The Hermit"),
        Quest("The Huntsman Path - Administrator"), Quest("The Punisher - Part 2"),
        Quest("The Tarkov Butcher"), Quest("Top Secret"),
    ];

    private static QuestTaskDefinition Quest(string name) => new(
        name.ToLowerInvariant().Replace(' ', '-'), name, null, null, null, null, null, null, null,
        null, null, null, null, [], [], [], [], "{}");
}
