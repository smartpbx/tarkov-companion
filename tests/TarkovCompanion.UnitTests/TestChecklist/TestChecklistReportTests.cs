using TarkovCompanion.App.Services.TestChecklist;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.UnitTests.TestChecklist;

public sealed class TestChecklistReportTests
{
    private static TestChecklistItem Item(string id, string feature) =>
        new(id, "Raid", feature, ["game"], ["Start a raid", "Look at NOW"], "NOW reads the exit.", "#/raid", null, []);

    [Fact]
    public void Broken_and_needs_work_come_first_with_notes_and_steps_then_untested_then_one_line_each()
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        TestChecklistItem[] items =
        [
            Item("raid.works", "Works one"),
            Item("raid.untested", "Untested one"),
            Item("raid.broken", "Broken one"),
            Item("raid.needs", "Needs one"),
            Item("raid.skipped", "Skipped one"),
        ];
        var results = new Dictionary<string, TestChecklistResult>
        {
            ["raid.works"] = new(TestStatus.Works, null, "2.0.1399", now),
            ["raid.broken"] = new(TestStatus.Broken, "Shows the wrong exit\nevery time", "2.0.1400", now),
            ["raid.needs"] = new(TestStatus.NeedsWork, "Too small", "2.0.1400", now),
            ["raid.skipped"] = new(TestStatus.Skipped, null, "2.0.1400", now),
        };

        var markdown = TestChecklistReport.Markdown(items, results, "2.0.1400", now);

        Assert.StartsWith("# Test checklist results", markdown, StringComparison.Ordinal);
        Assert.Contains($"Build 2.0.1400 · {LocalTime.Moment(now)}", markdown, StringComparison.Ordinal);
        Assert.Contains("4 of 5 tested · 1 work · 1 broken · 1 need work · 1 skipped · 1 to retest", markdown, StringComparison.Ordinal);
        string[] order = ["## Broken (1)", "### Raid › Broken one (`raid.broken`)", "Note: Shows the wrong exit / every time",
            "1. Start a raid", "2. Look at NOW", "Expected: NOW reads the exit.", "## Needs work (1)", "Note: Too small",
            "## Untested (1)", "- Raid › Untested one (`raid.untested`)", "## Works (1)",
            "- Raid › Works one (`raid.works`) · retest, recorded on 2.0.1399", "## Skipped (1)", "- Raid › Skipped one (`raid.skipped`)"];
        var at = order.Select(line => markdown.IndexOf(line, StringComparison.Ordinal)).ToArray();
        Assert.All(at.Zip(order), pair => Assert.True(pair.First >= 0, $"missing: {pair.Second}"));
        Assert.Equal(at.Order(), at);
    }

    [Fact]
    public void The_summary_counts_only_items_that_ship()
    {
        var results = new Dictionary<string, TestChecklistResult>
        {
            ["gone.item"] = new(TestStatus.Broken, null, "b", DateTimeOffset.UtcNow),
        };

        var summary = TestChecklistSummary.Of([Item("raid.one", "One")], results, "b");

        Assert.Equal((1, 0, 0), (summary.Total, summary.Tested, summary.Broken));
    }

    [Fact]
    public void The_flag_is_on_for_dev_and_rough_and_off_for_stable()
    {
        Assert.True(Flag.TestChecklist.DefaultFor(ReleaseRing.Dev));
        Assert.True(Flag.TestChecklist.DefaultFor(ReleaseRing.Rough));
        Assert.False(Flag.TestChecklist.DefaultFor(ReleaseRing.Stable));
        Assert.Contains(Flag.TestChecklist, Flag.All);
        Assert.Equal(Flag.TestChecklist, TarkovCompanion.App.Services.V2.Shell.V2SettingsIndex.All.Single(entry => entry.Id == "test-checklist").Flag);
    }
}
