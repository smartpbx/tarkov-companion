using System.Text;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.Services.TestChecklist;

/// <summary>The counts the page's progress line and the report's summary share.</summary>
public sealed record TestChecklistSummary(int Total, int Tested, int Works, int Broken, int NeedsWork, int Skipped, int Retest)
{
    public int Untested => Total - Tested;

    public static TestChecklistSummary Of(
        IReadOnlyList<TestChecklistItem> items,
        IReadOnlyDictionary<string, TestChecklistResult> results,
        string runningBuild)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(results);
        int works = 0, broken = 0, needsWork = 0, skipped = 0, retest = 0;
        foreach (var item in items)
        {
            if (results.GetValueOrDefault(item.Id) is not { } result)
            {
                continue;
            }

            switch (result.Status)
            {
                case TestStatus.Works: works++; break;
                case TestStatus.Broken: broken++; break;
                case TestStatus.NeedsWork: needsWork++; break;
                case TestStatus.Skipped: skipped++; break;
            }

            if (result.NeedsRetest(runningBuild))
            {
                retest++;
            }
        }

        return new(items.Count, works + broken + needsWork + skipped, works, broken, needsWork, skipped, retest);
    }
}

/// <summary>
/// The results as Markdown, for pasting into an issue or a chat: what is broken and what needs work
/// first, with the notes and the steps that got there, then what is untested, then one line each for
/// what works and what was skipped.
/// </summary>
public static class TestChecklistReport
{
    public static string Markdown(
        IReadOnlyList<TestChecklistItem> items,
        IReadOnlyDictionary<string, TestChecklistResult> results,
        string runningBuild,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(results);
        var summary = TestChecklistSummary.Of(items, results, runningBuild);
        var text = new StringBuilder();
        text.AppendLine("# Test checklist results");
        text.AppendLine();
        text.AppendLine($"Build {runningBuild} · {LocalTime.Moment(now)}");
        text.AppendLine();
        text.AppendLine($"{summary.Tested} of {summary.Total} tested · {summary.Works} work · {summary.Broken} broken · "
            + $"{summary.NeedsWork} need work · {summary.Skipped} skipped · {summary.Retest} to retest");

        Detailed(text, "Broken", items.Where(item => StatusFor(item) == TestStatus.Broken), results, runningBuild);
        Detailed(text, "Needs work", items.Where(item => StatusFor(item) == TestStatus.NeedsWork), results, runningBuild);
        Listed(text, "Untested", items.Where(item => StatusFor(item) == TestStatus.Untested), results, runningBuild);
        Listed(text, "Works", items.Where(item => StatusFor(item) == TestStatus.Works), results, runningBuild);
        Listed(text, "Skipped", items.Where(item => StatusFor(item) == TestStatus.Skipped), results, runningBuild);
        return text.ToString();

        TestStatus StatusFor(TestChecklistItem item) => results.GetValueOrDefault(item.Id)?.Status ?? TestStatus.Untested;
    }

    private static void Detailed(
        StringBuilder text,
        string heading,
        IEnumerable<TestChecklistItem> items,
        IReadOnlyDictionary<string, TestChecklistResult> results,
        string runningBuild)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"## {heading} ({list.Count})");
        foreach (var item in list)
        {
            var result = results[item.Id];
            text.AppendLine();
            text.AppendLine($"### {item.Area} › {item.Feature} (`{item.Id}`)");
            text.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                text.AppendLine($"Note: {OneLine(result.Note)}");
                text.AppendLine();
            }

            for (var step = 0; step < item.Steps.Count; step++)
            {
                text.AppendLine($"{step + 1}. {item.Steps[step]}");
            }

            text.AppendLine();
            text.AppendLine($"Expected: {item.Expect}");
            text.AppendLine();
            text.AppendLine(Recorded(result, runningBuild));
        }
    }

    private static void Listed(
        StringBuilder text,
        string heading,
        IEnumerable<TestChecklistItem> items,
        IReadOnlyDictionary<string, TestChecklistResult> results,
        string runningBuild)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"## {heading} ({list.Count})");
        text.AppendLine();
        foreach (var item in list)
        {
            var line = $"- {item.Area} › {item.Feature} (`{item.Id}`)";
            if (results.GetValueOrDefault(item.Id) is { } result)
            {
                if (result.NeedsRetest(runningBuild))
                {
                    line += $" · retest, recorded on {result.Build ?? "an unknown build"}";
                }

                if (!string.IsNullOrWhiteSpace(result.Note))
                {
                    line += $" · {OneLine(result.Note)}";
                }
            }

            text.AppendLine(line);
        }
    }

    private static string Recorded(TestChecklistResult result, string runningBuild)
    {
        var when = result.TestedUtc is { } tested ? $" at {LocalTime.Moment(tested)}" : string.Empty;
        var retest = result.NeedsRetest(runningBuild) ? " · retest on this build" : string.Empty;
        return $"_Recorded on build {result.Build ?? "unknown"}{when}{retest}_";
    }

    private static string OneLine(string note) =>
        string.Join(" / ", note.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>What the checklist page shows.</summary>
public enum TestChecklistFilter
{
    All,
    Untested,
    Broken,
    NeedsWork,
    Retest,
}
