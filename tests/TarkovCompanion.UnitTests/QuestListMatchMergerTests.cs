using TarkovCompanion.Application.Services.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestListMatchMergerTests
{
    private readonly QuestListMatchMerger _merger = new();

    [Fact]
    public void RepeatedQuestKeepsTheStrongestReadingAtItsFirstPosition()
    {
        var result = _merger.Merge(
        [
            Matched("Sh0rtage", "shortage", "Shortage", 0.88),
            Matched("Debut", "debut", "Debut", 1),
            Matched("Shortage", "shortage", "Shortage", 1),
        ]);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(["shortage", "debut"], result.Matched.Select(line => line.Confirmed!.TaskId));
        Assert.Equal("Shortage", result.Lines[0].OcrLine);
        Assert.Equal(1, result.Lines[0].Confirmed!.Confidence);
    }

    [Fact]
    public void RepeatedAmbiguityAndHeaderNoiseCollapseButDistinctEvidenceRemains()
    {
        var gunsmith = Ambiguous("Gunsmith Part", ("g1", "Gunsmith - Part 1"), ("g2", "Gunsmith - Part 2"));
        var result = _merger.Merge(
        [
            gunsmith,
            gunsmith with { OcrLine = "GUNSMITH—PART" },
            Ambiguous("Postman Pat Part", ("p1", "Postman Pat - Part 1"), ("p2", "Postman Pat - Part 2")),
            Unmatched("CHARACTER TASKS"),
            Unmatched("character   tasks"),
        ]);

        Assert.Equal(2, result.Ambiguous.Count);
        Assert.Single(result.Unmatched);
        Assert.Equal(3, result.Lines.Count);
    }

    private static QuestListLineMatch Matched(
        string line,
        string taskId,
        string name,
        double confidence) =>
        new(line, QuestListLineKind.Matched, [new(taskId, name, confidence)]);

    private static QuestListLineMatch Ambiguous(
        string line,
        params (string Id, string Name)[] candidates) =>
        new(
            line,
            QuestListLineKind.Ambiguous,
            candidates.Select(candidate => new QuestListCandidate(candidate.Id, candidate.Name, 0.75)).ToArray());

    private static QuestListLineMatch Unmatched(string line) =>
        new(line, QuestListLineKind.Unmatched, []);
}
