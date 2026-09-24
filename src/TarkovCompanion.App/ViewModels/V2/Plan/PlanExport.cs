using System.Globalization;
using System.Text;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One numbered step of an exported plan.</summary>
public sealed record PlanExportStep(
    int Number,
    string Quest,
    string Trader,
    string Objective,
    string Handling,
    string Remaining);

/// <summary>One map's worth of an exported plan.</summary>
public sealed record PlanExportGroup(
    string MapLabel,
    IReadOnlyList<PlanExportStep> Steps,
    IReadOnlyList<string> StillNeeded);

/// <summary>One item the whole plan still asks for, wherever it is needed.</summary>
/// <param name="Have">The recorded holding, or null where none is recorded; nothing is taken off an unknown.</param>
public sealed record PlanExportItem(string ItemName, string Handling, int Need, int? Have)
{
    public int Short => HeldCount.Remaining(Need, Have);
}

/// <summary>
/// A quest that cannot be started yet, and the chain standing in front of it.
/// </summary>
/// <param name="Depth">
/// How many incomplete quests deep the longest chain behind this one runs. Zero means nothing is
/// in the way, which is what makes it a starting point rather than a step.
/// </param>
/// <param name="WaitingOn">Its own immediate unmet prerequisites, named where the plan knows them.</param>
public sealed record PlanExportBlocked(string Quest, int Depth, IReadOnlyList<string> WaitingOn);

/// <summary>
/// A plan as text somebody can keep, print or paste to a squadmate.
/// </summary>
/// <remarks>
/// Every figure here is one already on the screen; nothing is recomputed and nothing is
/// estimated that the page did not already estimate. The generated line and the scope line are
/// part of the document rather than a footer, because a plan pasted into a chat window three
/// hours later is otherwise indistinguishable from a current one.
/// </remarks>
public sealed record PlanExportDocument(
    DateTimeOffset GeneratedUtc,
    string Scope,
    string Filter,
    string? Search,
    IReadOnlyList<PlanExportGroup> Groups,
    IReadOnlyList<PlanExportItem> ShoppingList,
    IReadOnlyList<PlanExportBlocked> CriticalPath,
    string Freshness)
{
    /// <summary>The whole plan as Markdown, which reads as text and prints as a document.</summary>
    public string ToMarkdown(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var text = new StringBuilder();
        text.Append("# ").Append(PlanText.ExportTitle).Append("\n\n");
        // Local time, not UTC: this line is read by a person deciding whether the plan is stale,
        // and every other user-facing timestamp in the application is their own clock.
        text.AppendFormat(CultureInfo.InvariantCulture, PlanText.ExportGeneratedPattern, LocalTime.ToLocal(GeneratedUtc).ToString("f", culture));
        text.AppendFormat(CultureInfo.InvariantCulture, PlanText.ExportScopePattern, Scope, Filter);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            text.AppendFormat(CultureInfo.InvariantCulture, PlanText.ExportSearchPattern, Search);
        }

        text.Append("\n\n");

        if (Groups.Count == 0)
        {
            text.Append(PlanText.ExportNothingPlanned).Append('\n');
            return text.ToString();
        }

        foreach (var group in Groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"## {group.MapLabel}\n\n");
            foreach (var step in group.Steps)
            {
                text.Append(CultureInfo.InvariantCulture, $"{step.Number}. **{step.Quest}** ({step.Trader}) — {step.Objective}");
                var notes = new[] { step.Handling, step.Remaining }.Where(note => note.Length > 0).ToArray();
                if (notes.Length > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $" · {string.Join(" · ", notes)}");
                }

                text.Append('\n');
            }

            if (group.StillNeeded.Count > 0)
            {
                text.Append("\n   ").Append(PlanText.ExportStillNeeded).Append(' ');
                text.Append(string.Join(", ", group.StillNeeded));
                text.Append('\n');
            }

            text.Append('\n');
        }

        if (ShoppingList.Count > 0)
        {
            text.Append("## ").Append(PlanText.ExportShoppingList).Append("\n\n");
            foreach (var item in ShoppingList)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {item.Short:N0}x {item.ItemName} ({item.Handling.ToLower(culture)})");
                if (item.Have is null)
                {
                    // The full need is listed because nothing is known that would make it less, and
                    // the line says so: read without it, "5x" claims the player holds none.
                    text.Append(" · ").Append(PlanText.ExportHeldUnknown);
                }
                else if (item.Have > 0)
                {
                    text.Append(" · ").AppendFormat(CultureInfo.InvariantCulture, PlanText.ExportAlreadyHeldPattern, item.Have, item.Need);
                }

                text.Append('\n');
            }

            text.Append('\n');
        }

        if (CriticalPath.Count > 0)
        {
            text.Append("## ").Append(PlanText.ExportInTheWay).Append("\n\n");
            foreach (var blocked in CriticalPath)
            {
                text.Append(CultureInfo.InvariantCulture, $"- **{blocked.Quest}** — {Describe(blocked, culture)}\n");
            }

            text.Append('\n');
        }

        text.Append(CultureInfo.InvariantCulture, $"_{Freshness}_\n");
        return text.ToString();
    }

    private static string Describe(PlanExportBlocked blocked, CultureInfo culture) => blocked.Depth switch
    {
        0 => PlanText.ExportNothingInTheWay,
        1 => string.Format(culture, PlanText.ExportWaitingOnPattern, string.Join(", ", blocked.WaitingOn)),
        var depth => string.Format(culture, PlanText.ExportQuestsDeepPattern, depth, string.Join(", ", blocked.WaitingOn)),
    };
}

/// <summary>
/// Turns the Plan workspace as it stands into a document (#288's plan export, #315's export).
/// </summary>
/// <remarks>
/// A pure function of the view model so it can be tested without a window, a profile or a map.
/// It reads only what the page has already computed and shown, which is the whole point: an
/// export that recomputed anything could disagree with the screen it claims to be a copy of.
/// </remarks>
public static class PlanExport
{
    /// <summary>How many blocked quests are worth listing before the list stops being read.</summary>
    private const int MaximumBlocked = 12;

    /// <param name="nameOfTask">
    /// Names a quest that is not itself in the plan. A prerequisite is no less in the way for
    /// being off-screen, and "waiting on 66058cbd9f59e625462acc8e" tells a reader nothing.
    /// </param>
    public static PlanExportDocument Build(
        PlanWorkspaceViewModel plan,
        DateTimeOffset nowUtc,
        Func<string, string>? nameOfTask = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var groups = plan.Groups
            .Select(group => new PlanExportGroup(
                group.MapLabel,
                [.. group.VisitOrder.Select(Step)],
                [
                    .. group.Requirements
                        .Where(requirement => !requirement.IsSatisfied)
                        .Select(requirement => string.Create(
                            CultureInfo.CurrentCulture,
                            $"{HeldCount.Remaining(requirement.Need, requirement.Have):N0}x {requirement.ItemName}")),
                ]))
            .ToArray();

        return new(
            nowUtc,
            plan.ScopeLabel,
            plan.Filter.ToString(),
            plan.HasSearchText ? plan.SearchText.Trim() : null,
            groups,
            ShoppingList(plan.Groups.SelectMany(group => group.Requirements)),
            CriticalPath(PlannedQuests(plan), nameOfTask),
            plan.HasRequirementsRollup ? plan.RequirementsRollup : plan.Status);
    }

    private static PlanExportStep Step(PlanObjectiveRowViewModel row) => new(
        row.Number,
        row.TaskName,
        row.TraderLabel,
        row.Description,
        row.HandlingLabel,
        row.HasRemainingLabel ? row.RemainingLabel : string.Empty);

    private static IReadOnlyList<QuestSummaryReadModel> PlannedQuests(PlanWorkspaceViewModel plan) =>
    [
        .. plan.Groups
            .SelectMany(group => group.VisitOrder)
            .Select(row => row.Task)
            .DistinctBy(task => task.TaskId, StringComparer.Ordinal),
    ];

    /// <summary>
    /// One pile per item, however many maps ask for it.
    /// </summary>
    /// <remarks>
    /// Needs add up because two maps asking for three each is six to carry; held counts do not,
    /// because the same stash is counted on both rows and adding them would report twice what
    /// the player owns. Satisfied rows are left out: a shopping list is what is still missing.
    /// </remarks>
    internal static IReadOnlyList<PlanExportItem> ShoppingList(IEnumerable<PlanRequirementRowViewModel> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        return
        [
            .. requirements
                // By id, not by name: two items the catalog does not know share one display name,
                // and grouping on it reported needing two of a single thing that does not exist.
                .GroupBy(row => (row.ItemId, row.HandlingLabel))
                .Select(group => new PlanExportItem(
                    group.First().ItemName,
                    group.Key.HandlingLabel,
                    group.Sum(row => row.Need),
                    group.Max(row => row.Have)))
                .Where(item => item.Short > 0)
                .OrderByDescending(item => item.Short)
                .ThenBy(item => item.ItemName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>
    /// Which planned quests can be started now, and which are waiting behind how much.
    /// </summary>
    /// <remarks>
    /// The critical path is the longest chain of unfinished prerequisites, because that chain is
    /// what decides when the last quest in the plan can finish; shortening anything else changes
    /// nothing. Depth is measured over the catalog's own prerequisite records, so a prerequisite
    /// the plan does not itself contain still counts — it is no less in the way for being
    /// off-screen. A prerequisite already recorded complete counts for nothing.
    ///
    /// Cycles are guarded rather than trusted away: the catalog is external data, a quest that
    /// required itself would otherwise recurse until the stack ran out, and "treat as depth 0"
    /// is the answer that still draws a page.
    /// </remarks>
    internal static IReadOnlyList<PlanExportBlocked> CriticalPath(
        IReadOnlyList<QuestSummaryReadModel> quests,
        Func<string, string>? nameOfTask = null)
    {
        ArgumentNullException.ThrowIfNull(quests);
        var byId = quests.ToDictionary(quest => quest.TaskId, StringComparer.Ordinal);
        var planned = quests.ToDictionary(quest => quest.TaskId, quest => quest.Name, StringComparer.Ordinal);
        string Name(string taskId) => planned.TryGetValue(taskId, out var name)
            ? name
            : nameOfTask?.Invoke(taskId) is { Length: > 0 } resolved ? resolved : taskId;
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(string taskId, HashSet<string> visiting)
        {
            if (depths.TryGetValue(taskId, out var known))
            {
                return known;
            }

            if (!visiting.Add(taskId))
            {
                return 0;
            }

            var unmet = byId.TryGetValue(taskId, out var quest)
                ? quest.Prerequisites.Where(IsUnmet).ToArray()
                : [];
            var depth = unmet.Length == 0
                ? 0
                : unmet.Max(prerequisite => 1 + DepthOf(prerequisite.RequiredTaskId, visiting));
            visiting.Remove(taskId);
            depths[taskId] = depth;
            return depth;
        }

        return
        [
            .. quests
                .Select(quest => new PlanExportBlocked(
                    quest.Name,
                    DepthOf(quest.TaskId, []),
                    [
                        .. quest.Prerequisites
                            .Where(IsUnmet)
                            .Select(prerequisite => Name(prerequisite.RequiredTaskId)),
                    ]))
                .Where(blocked => blocked.WaitingOn.Count > 0)
                .OrderByDescending(blocked => blocked.Depth)
                .ThenBy(blocked => blocked.Quest, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumBlocked),
        ];
    }

    private static bool IsUnmet(QuestPrerequisiteReadModel prerequisite) =>
        prerequisite.RecordedState != RecordedTaskState.Completed;
}
