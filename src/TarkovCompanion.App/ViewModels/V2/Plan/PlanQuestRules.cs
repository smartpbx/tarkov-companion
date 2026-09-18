using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>Which quests the Plan workspace plans around.</summary>
public enum PlanQuestFilter
{
    /// <summary>Active quests, and any quest or objective the player pinned.</summary>
    Active,

    /// <summary>Not started, and the catalog says the player can take it now.</summary>
    Available,

    /// <summary>Not started, and gated by a level or an unfinished prerequisite.</summary>
    Locked,

    Completed,

    /// <summary>Quests the catalog says Kappa needs, until they are done.</summary>
    Kappa,

    All,
}

/// <summary>One filter chip in the Plan column.</summary>
public sealed class PlanFilterChipViewModel : BindableViewModel
{
    private bool _isSelected;

    internal PlanFilterChipViewModel(PlanQuestFilter filter, Action<PlanQuestFilter> select)
    {
        Filter = filter;
        Label = PlanQuestRules.FilterLabel(filter);
        SelectCommand = new DelegateCommand(() => select(filter));
    }

    public PlanQuestFilter Filter { get; }

    public string Label { get; }

    public string AutomationId => $"v2-plan-filter-{Filter.ToString().ToLowerInvariant()}";

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One choice in the trader filter; a null id is "every trader".</summary>
public sealed record PlanTraderOption(string? TraderId, string Name);

/// <summary>One trader's loyalty level, edited in the profile section.</summary>
public sealed record PlanTraderLoyaltyViewModel(string TraderId, string Name, decimal Level);

/// <summary>One item a map's objectives ask the player to bring, hand in or find.</summary>
/// <param name="HandlingLabel">"Bring", "Hand in" or "Find in raid".</param>
public sealed record PlanRequirementRowViewModel(
    string ItemName,
    string HandlingLabel,
    int Need,
    int Have)
{
    public bool IsSatisfied => Have >= Need;

    /// <summary>"2 / 5": held against needed, the right-hand figure of the row.</summary>
    public string ProgressLabel => string.Create(CultureInfo.CurrentCulture, $"{Math.Min(Have, Need):N0} / {Need:N0}");
}

/// <summary>
/// The decisions the Plan workspace makes about which quests to show and what they ask for,
/// kept free of services so they can be tested without a profile or a map.
/// </summary>
public static class PlanQuestRules
{
    public static string FilterLabel(PlanQuestFilter filter) => filter switch
    {
        PlanQuestFilter.Active => "Active",
        PlanQuestFilter.Available => "Available now",
        PlanQuestFilter.Locked => "Locked",
        PlanQuestFilter.Completed => "Completed",
        PlanQuestFilter.Kappa => "Kappa",
        _ => "All",
    };

    /// <summary>Whether a quest belongs to a filter. The filters never overlap on state: a started quest is Active, not Available.</summary>
    public static bool Includes(QuestSummaryReadModel task, PlanQuestFilter filter)
    {
        ArgumentNullException.ThrowIfNull(task);
        var notStarted = task.RecordedState is RecordedTaskState.Unknown or RecordedTaskState.NotStarted;
        return filter switch
        {
            PlanQuestFilter.Active => task.RecordedState == RecordedTaskState.Active ||
                task.IsPinned ||
                task.Objectives.Any(objective => objective.IsPinned),
            PlanQuestFilter.Available => notStarted && task.Eligibility.State == QuestEligibilityState.Available,
            PlanQuestFilter.Locked => notStarted && task.Eligibility.State == QuestEligibilityState.Locked,
            PlanQuestFilter.Completed => task.RecordedState == RecordedTaskState.Completed,
            PlanQuestFilter.Kappa => task.KappaRequired == true && task.RecordedState != RecordedTaskState.Completed,
            _ => true,
        };
    }

    /// <summary>Completed quests plan nothing, so their objectives are shown only where the player asked to see finished work.</summary>
    public static bool ShowsFinishedObjectives(PlanQuestFilter filter) => filter is PlanQuestFilter.Completed or PlanQuestFilter.All;

    /// <summary>
    /// What a quest is doing, for the row, or empty for one already being played.
    /// </summary>
    /// <remarks>
    /// "Indeterminate" is an accurate name for a state the evaluator can reach and a useless
    /// thing to show somebody deciding what to do next, so it reads as nothing rather than as
    /// a word: the quest is neither known to be available nor known to be locked.
    /// </remarks>
    public static string DescribeStatus(QuestSummaryReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.RecordedState switch
        {
            RecordedTaskState.Active => string.Empty,
            RecordedTaskState.Completed => "Completed",
            RecordedTaskState.Failed => "Failed",
            _ => task.Eligibility.State switch
            {
                QuestEligibilityState.Available => "Available now",
                QuestEligibilityState.Locked => task.Eligibility.Reasons.Count == 0
                    ? "Locked"
                    : $"Locked · {task.Eligibility.Reasons[0].Detail}",
                QuestEligibilityState.Delayed => "Waiting on a timer",
                _ => string.Empty,
            },
        };
    }

    /// <summary>Why a filter and a search left nothing, so the empty column can say what to change.</summary>
    public static string DescribeEmpty(PlanQuestFilter filter, string query, bool hasTraderFilter) => (query.Length, hasTraderFilter, filter) switch
    {
        (> 0, _, _) => $"No quest matches “{query}” in {FilterLabel(filter)}.",
        (_, true, _) => $"No {FilterLabel(filter).ToLowerInvariant()} quest for this trader.",
        (_, _, PlanQuestFilter.Active) => "No active quests. Try Available now, or All.",
        (_, _, PlanQuestFilter.Available) => "Nothing is available right now.",
        (_, _, PlanQuestFilter.Locked) => "No quest is locked.",
        (_, _, PlanQuestFilter.Completed) => "No quest is recorded complete.",
        (_, _, PlanQuestFilter.Kappa) => "No Kappa quest is left, or the catalog does not say which are.",
        _ => "No quests recorded yet.",
    };

    /// <summary>
    /// What a set of objectives asks the player to have on them, with how much of it they hold.
    /// </summary>
    /// <remarks>
    /// Keys, weapons, worn gear and markers are carried in, one of each; everything else is
    /// handed over, as many as the objective still needs. Alternatives ("this or that") are one
    /// requirement satisfied by any of them, named by the first with the rest counted. The same
    /// item asked for by two objectives is one row, because the player holds one pile of it.
    /// "Not wearing" and container-content conditions name nothing to have, so they are left out.
    /// </remarks>
    public static IReadOnlyList<PlanRequirementRowViewModel> BuildRequirements(
        IEnumerable<QuestObjectiveReadModel> objectives,
        Func<string, string> nameOf,
        IReadOnlyDictionary<string, int> owned)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(owned);

        var rows = new Dictionary<(string ItemId, string Handling), (string Name, int Need, int Have)>();
        foreach (var objective in objectives.Where(objective => objective.RecordedState != RecordedObjectiveState.Completed))
        {
            var targets = objective.ItemTargets.Where(target => !IsCondition(target.SourceField));
            foreach (var alternatives in targets.GroupBy(target => (target.SourceField, target.AlternativeGroup)))
            {
                var ids = alternatives
                    .OrderBy(target => target.SourceOrdinal)
                    .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                    .Select(target => target.ItemId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var carried = QuestItemRequirementFormatter.IsCarriedIn(alternatives.Key.SourceField);
                var handling = carried
                    ? "Bring"
                    : objective.FoundInRaidRequired == true ? "Find in raid" : "Hand in";
                var need = carried
                    ? 1
                    : (int)Math.Ceiling(Math.Max(
                        1m,
                        (alternatives.Max(target => target.TargetCount) ?? objective.TargetCount ?? 1m) - (objective.RecordedCount ?? 0m)));
                var have = ids.Sum(id => owned.GetValueOrDefault(id));
                var name = ids.Length == 1
                    ? nameOf(ids[0])
                    : string.Create(CultureInfo.CurrentCulture, $"{nameOf(ids[0])} or {ids.Length - 1:N0} more");
                var key = (ids[0], handling);
                rows[key] = rows.TryGetValue(key, out var existing)
                    ? (existing.Name, carried ? existing.Need : existing.Need + need, existing.Have)
                    : (name, need, have);
            }
        }

        return
        [
            .. rows
                .Select(row => new PlanRequirementRowViewModel(row.Value.Name, row.Key.Handling, row.Value.Need, row.Value.Have))
                .OrderBy(row => row.IsSatisfied)
                .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    private static bool IsCondition(string sourceField) =>
        sourceField is "notWearing" or "attributes" or "containsAll" or "containsOne";
}
