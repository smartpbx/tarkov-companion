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
