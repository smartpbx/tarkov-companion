using System.Globalization;
using TarkovCompanion.App.Services.TestChecklist;

namespace TarkovCompanion.App.Localization;

/// <summary>Every word Setup › Test checklist shows, from Localization/Strings (#314).</summary>
/// <remarks>The items themselves are data (Assets/TestChecklist) and are shown as written.</remarks>
public static class TestChecklistText
{
    public static string Heading => UiText.Get("TestChecklist.Heading");
    public static string Intro => UiText.Get("TestChecklist.Intro");
    public static string Filters => UiText.Get("TestChecklist.Filters");
    public static string Nothing => UiText.Get("TestChecklist.Nothing");
    public static string NoItems => UiText.Get("TestChecklist.NoItems");
    public static string Expect => UiText.Get("TestChecklist.Expect");
    public static string GoThere => UiText.Get("TestChecklist.GoThere");
    public static string Works => UiText.Get("TestChecklist.Works");
    public static string Broken => UiText.Get("TestChecklist.Broken");
    public static string NeedsWork => UiText.Get("TestChecklist.NeedsWork");
    public static string Skip => UiText.Get("TestChecklist.Skip");
    public static string Retest => UiText.Get("TestChecklist.Retest");
    public static string StatusGroup => UiText.Get("TestChecklist.StatusGroup");
    public static string Note => UiText.Get("TestChecklist.Note");
    public static string NoteHint => UiText.Get("TestChecklist.NoteHint");
    public static string Copy => UiText.Get("TestChecklist.Copy");
    public static string Export => UiText.Get("TestChecklist.Export");
    public static string Clear => UiText.Get("TestChecklist.Clear");
    public static string ClearConfirm => UiText.Get("TestChecklist.ClearConfirm");
    public static string ClearYes => UiText.Get("TestChecklist.ClearYes");
    public static string ClearNo => UiText.Get("TestChecklist.ClearNo");
    public static string Copied => UiText.Get("TestChecklist.Copied");
    public static string Exported => UiText.Get("TestChecklist.Exported");
    public static string Cleared => UiText.Get("TestChecklist.Cleared");
    public static string ExportTitle => UiText.Get("TestChecklist.ExportTitle");
    public static string Open => UiText.Get("TestChecklist.Open");
    public static string Search => UiText.Get("TestChecklist.Search");
    public static string SearchHint => UiText.Get("TestChecklist.SearchHint");
    public static string NextUntested => UiText.Get("TestChecklist.NextUntested");
    public static string NoneUntested => UiText.Get("TestChecklist.NoneUntested");
    public static string ExpandAll => UiText.Get("TestChecklist.ExpandAll");
    public static string Edit => UiText.Get("TestChecklist.Edit");
    public static string Fold => UiText.Get("TestChecklist.Fold");
    public static string Areas => UiText.Get("TestChecklist.Areas");
    public static string NeedFilters => UiText.Get("TestChecklist.NeedFilters");

    public static string RailCount(int tested, int total) => UiText.Format("TestChecklist.RailCount", tested, total);

    public static string RailBroken(int count) => UiText.Format("TestChecklist.RailBroken", count);

    public static string RailNeedsWork(int count) => UiText.Format("TestChecklist.RailNeedsWork", count);

    public static string NeedFilterLabel(TestChecklistNeedFilter filter, int count) =>
        filter == TestChecklistNeedFilter.Any
            ? UiText.Get("TestChecklist.Need.Filter.Any")
            : UiText.Format("TestChecklist.Filter.WithCount", UiText.Get($"TestChecklist.Need.Filter.{filter}"), count);

    public static string SaveFailed(string reason) => UiText.Format("TestChecklist.SaveFailed", reason);

    public static string Progress(TestChecklistSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var line = UiText.Format("TestChecklist.Progress", summary.Tested, summary.Total, summary.Broken, summary.NeedsWork);
        return summary.Retest > 0 ? UiText.Format("TestChecklist.ProgressRetest", line, summary.Retest) : line;
    }

    public static string Unreadable(int count) => UiText.Plural("TestChecklist.Unreadable", count);

    public static string AreaCount(int tested, int total) => UiText.Format("TestChecklist.AreaCount", tested, total);

    public static string FilterLabel(TestChecklistFilter filter, int count) =>
        filter == TestChecklistFilter.All
            ? UiText.Get("TestChecklist.Filter.All")
            : UiText.Format("TestChecklist.Filter.WithCount", UiText.Get($"TestChecklist.Filter.{filter}"), count);

    public static string Status(TestStatus status) => status switch
    {
        TestStatus.Works => UiText.Get("TestChecklist.Works"),
        TestStatus.Broken => UiText.Get("TestChecklist.Broken"),
        TestStatus.NeedsWork => UiText.Get("TestChecklist.NeedsWork"),
        TestStatus.Skipped => UiText.Get("TestChecklist.Skipped"),
        _ => UiText.Get("TestChecklist.Untested"),
    };

    /// <summary>"Broken on 2.0.1400 · 28/09/2026 17:40", the time through LocalTime.</summary>
    public static string Recorded(TestStatus status, string build, DateTimeOffset testedUtc) =>
        UiText.Format("TestChecklist.Recorded", Status(status), build, TarkovCompanion.Core.Common.LocalTime.Moment(testedUtc, CultureInfo.CurrentCulture));

    public static string Flag(string key) => UiText.Format("TestChecklist.Flag", key);

    public static string Need(string need) => UiText.Get($"TestChecklist.Need.{need}");
}
