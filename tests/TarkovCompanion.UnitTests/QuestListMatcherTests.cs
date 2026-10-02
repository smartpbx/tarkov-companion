using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestListMatcherTests
{
    private readonly QuestListMatcher _matcher = new();

    [Theory]
    [InlineData("  THE TARKOV SHOOTER—PART 1 ", "tarkov-shooter-1")]
    [InlineData("Sh0rtage", "shortage")]
    [InlineData("Postrnan Pat - Part l", "postman-pat-1")]
    [InlineData("The Tarkov Shoter Part 1", "tarkov-shooter-1")]
    [InlineData("Balancing - Part 1 [Season PvP]", "balancing-1")]
    public void RealCatalogNamesSurvivePunctuationGlyphAndDroppedCharacterNoise(
        string line,
        string expectedTaskId)
    {
        var result = _matcher.Match([line], Catalog());

        var match = Assert.Single(result.Matched);
        Assert.Equal(expectedTaskId, match.Confirmed?.TaskId);
        Assert.True(match.Confirmed?.Confidence >= 0.78);
    }

    [Fact]
    public void PartNumberLostKeepsRankedCandidatesForConfirmation()
    {
        var result = _matcher.Match(["Gunsmith Part"], Catalog());

        var ambiguous = Assert.Single(result.Ambiguous);
        Assert.Equal(QuestListLineKind.Ambiguous, ambiguous.Kind);
        Assert.Equal(
            ["Gunsmith - Part 1", "Gunsmith - Part 2"],
            ambiguous.Candidates.Take(2).Select(candidate => candidate.QuestName));
        Assert.Equal(ambiguous.Candidates[0].Confidence, ambiguous.Candidates[1].Confidence);
    }

    [Fact]
    public void UnrelatedUiLineIsRetainedAsUnmatched()
    {
        var result = _matcher.Match(["TASKS  Character  Overall"], Catalog());

        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal("TASKS  Character  Overall", unmatched.OcrLine);
        Assert.Null(unmatched.Confirmed);
    }

    [Fact]
    public void EachNonBlankOcrLineProducesItsOwnResult()
    {
        var result = _matcher.Match(
            ["Debut", " ", "Golden Swag", "Ice Cream Cones"],
            Catalog());

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(
            ["debut", "golden-swag", "ice-cream-cones"],
            result.Matched.Select(match => match.Confirmed!.TaskId));
    }

    [Fact]
    public void CandidatesAreOrderedByConfidenceThenName()
    {
        var result = _matcher.Match(["The Survivalist Path Unprotected but Dangerou"], Catalog());

        var line = Assert.Single(result.Lines);
        Assert.Equal("survivalist-unprotected", line.Candidates[0].TaskId);
        Assert.True(line.Candidates.Zip(line.Candidates.Skip(1)).All(pair =>
            pair.First.Confidence >= pair.Second.Confidence));
    }

    /// <summary>The ten Not found lines from Clayton's sync (#989), as the issue quotes them.</summary>
    /// <remarks>
    /// None of these is in json.tarkov.dev today, in regular, pve or pvp-season (checked
    /// 2026-10-02): they are Season 1 KORD BREACH quests the feed does not publish. Against the
    /// real catalog they must stay Not found, never become a guess.
    /// </remarks>
    private static readonly string[] IssueNotFound =
    [
        "Honest Review", "Fog of War", "KORD BREACH] Unanswered Calls", "To the Light - Trust but Verify",
        "Invasive Therapy", "Stimulating Demand", "KORO BREACH] Uninvited Guests - Part 1",
    ];

    [Fact]
    public void SeasonQuestsAbsentFromTheCatalogStayNotFound()
    {
        var result = _matcher.Match(IssueNotFound, Catalog());

        Assert.Empty(result.Matched);
        Assert.Equal(IssueNotFound, result.Unmatched.Select(line => line.OcrLine));
    }

    [Theory]
    [InlineData("[KORD BREACH] Unanswered Calls")]
    [InlineData("Unanswered Calls [KORD BREACH]")]
    [InlineData("Unanswered Calls")]
    public void ABrokenLeadingEventTagStillMatchesTheQuest(string catalogName)
    {
        var catalog = SeasonCatalog(catalogName);

        foreach (var line in new[] { "KORD BREACH] Unanswered Calls", "[KORD BREACH] Unanswered Calls", "KORO BREACH] Unanswered Calls" })
        {
            var match = Assert.Single(_matcher.Match([line], catalog).Matched);
            Assert.Equal("unanswered-calls", match.Confirmed!.TaskId);
        }
    }

    [Theory]
    [InlineData("KORO BREACH] Uninvited Guests", "- Part 1", "uninvited-guests-1")]
    [InlineData("KORO BREACH] Uninvited Guests -", "Part 1", "uninvited-guests-1")]
    [InlineData("KORD BREACH] Uninvited Guests", "Part 2", "uninvited-guests-2")]
    [InlineData("KORO BREACH] Uninvited Guests - Part 1", null, "uninvited-guests-1")]
    public void ATitleWrappedOntoASecondLineIsJoined(string first, string? second, string expected)
    {
        var lines = second is null ? new[] { first } : [first, second];

        var result = _matcher.Match(lines, SeasonCatalog());

        var match = Assert.Single(result.Lines);
        Assert.Equal(QuestListLineKind.Matched, match.Kind);
        Assert.Equal(expected, match.Confirmed!.TaskId);
    }

    [Fact]
    public void ATitleWrappedMidNameIsJoinedWhenNeitherHalfIsAQuest()
    {
        var result = _matcher.Match(
            ["Honest Review", "To the Light - Trust", "but Verify", "Fog of War"],
            SeasonCatalog());

        Assert.Equal(
            ["honest-review", "to-the-light-trust-but-verify", "fog-of-war"],
            result.Lines.Select(line => line.Confirmed?.TaskId));
    }

    [Fact]
    public void TwoQuestsOnConsecutiveLinesAreNotJoined()
    {
        var result = _matcher.Match(["Invasive Therapy", "Stimulating Demand"], SeasonCatalog());

        Assert.Equal(["invasive-therapy", "stimulating-demand"], result.Matched.Select(line => line.Confirmed!.TaskId));
    }

    private static QuestTaskDefinition[] SeasonCatalog(string unansweredCalls = "[KORD BREACH] Unanswered Calls") =>
    [
        .. Catalog(),
        Task("unanswered-calls", unansweredCalls),
        Task("uninvited-guests-1", "[KORD BREACH] Uninvited Guests - Part 1"),
        Task("uninvited-guests-2", "[KORD BREACH] Uninvited Guests - Part 2"),
        Task("honest-review", "Honest Review"),
        Task("fog-of-war", "Fog of War"),
        Task("to-the-light-trust-but-verify", "To the Light - Trust but Verify"),
        Task("invasive-therapy", "Invasive Therapy"),
        Task("stimulating-demand", "Stimulating Demand"),
    ];

    private static QuestTaskDefinition[] Catalog() =>
    [
        Task("debut", "Debut"),
        Task("shortage", "Shortage"),
        Task("golden-swag", "Golden Swag"),
        Task("ice-cream-cones", "Ice Cream Cones"),
        Task("postman-pat-1", "Postman Pat - Part 1"),
        Task("tarkov-shooter-1", "The Tarkov Shooter - Part 1"),
        Task("gunsmith-1", "Gunsmith - Part 1"),
        Task("gunsmith-2", "Gunsmith - Part 2"),
        Task("balancing-1", "Balancing - Part 1 [PVP ZONE]"),
        Task("survivalist-unprotected", "The Survivalist Path - Unprotected but Dangerous"),
    ];

    private static QuestTaskDefinition Task(string id, string name) => new(
        id,
        name,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        [],
        [],
        [],
        [],
        "{}");
}
