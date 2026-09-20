using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;
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
/// <param name="ItemId">
/// The catalog id. Carried beside the name because the name is not an identity: an item the
/// catalog does not know is called "Item not in the catalog", and two different unknown items
/// keyed by that phrase merge into one row saying the player needs two of something that does
/// not exist. The plan export found exactly that.
/// </param>
/// <param name="HandlingLabel">"Bring", "Hand in" or "Find in raid".</param>
public sealed record PlanRequirementRowViewModel(
    string ItemId,
    string ItemName,
    string HandlingLabel,
    int Need,
    int? Have)
{
    public bool IsSatisfied => HeldCount.Meets(Need, Have);

    /// <summary>Whether any holding is recorded for it. Where none is, the row says so instead of "0".</summary>
    public bool IsHeldKnown => Have is not null;

    /// <summary>"Allergic · event name" where the Events page records an allergy to this food or medicine (#285).</summary>
    public string AllergyWarning { get; init; } = string.Empty;

    public bool HasAllergyWarning => AllergyWarning.Length > 0;

    /// <summary>"2 / 5": held against needed, the right-hand figure of the row; "? / 5" where the holding is not recorded.</summary>
    public string ProgressLabel => Have is { } have
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Min(have, Need):N0} / {Need:N0}")
        : string.Create(CultureInfo.CurrentCulture, $"? / {Need:N0}");
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
    public static string DescribeStatus(QuestSummaryReadModel task, Func<string, string?>? nameOfTask = null)
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
                // What opens it, not why it is shut (#288); the wording is QuestUnlockPlanner's.
                QuestEligibilityState.Locked => task.Eligibility.Reasons.Count == 0
                    ? "Locked"
                    : $"Locked · {QuestUnlockPlanner.Summarise(QuestUnlockPlanner.Steps(task, nameOfTask ?? (static _ => null)))}",
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
    /// What a set of objectives asks the player to have on them, with how much of it they hold,
    /// named and ordered for the row. The requirement arithmetic is
    /// <see cref="QuestRequirementPlanner"/>; this adds the words.
    /// </summary>
    /// <remarks>
    /// Alternatives ("this or that") are named by the first with the rest counted.
    /// </remarks>
    public static IReadOnlyList<PlanRequirementRowViewModel> BuildRequirements(
        IEnumerable<QuestObjectiveReadModel> objectives,
        Func<string, string> nameOf,
        IReadOnlyDictionary<string, int> owned,
        Func<QuestObjectiveReadModel, IReadOnlySet<string>>? handedOverByItsTask = null,
        IReadOnlyDictionary<string, string>? allergyWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(nameOf);

        return
        [
            .. QuestRequirementPlanner.Build(objectives, owned, handedOverByItsTask)
                .Select(requirement => new PlanRequirementRowViewModel(
                    requirement.PrimaryItemId,
                    requirement.AlternativeCount == 0
                        ? nameOf(requirement.PrimaryItemId)
                        : string.Create(CultureInfo.CurrentCulture, $"{nameOf(requirement.PrimaryItemId)} or {requirement.AlternativeCount:N0} more"),
                    HandlingLabel(requirement.Handling),
                    requirement.Need,
                    requirement.Have)
                {
                    // Any of the alternatives: the row offers all of them, so it warns for each.
                    AllergyWarning = allergyWarnings is null
                        ? string.Empty
                        : requirement.ItemIds
                            .Select(id => allergyWarnings.GetValueOrDefault(id))
                            .FirstOrDefault(warning => warning is not null) ?? string.Empty,
                })
                .OrderBy(row => row.IsSatisfied)
                .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>
    /// "2 still needed", "3 to check", or both: unmet requirements split by whether the shortfall
    /// is known. Empty where nothing is unmet.
    /// </summary>
    /// <remarks>
    /// A row whose holding nobody recorded is not known to be short. Counting it as "still needed"
    /// said the same false thing as "0 / 5", once per page instead of once per row. "To check" is
    /// what the player can actually do about it.
    /// </remarks>
    /// <param name="count">How a count is written: "6" by default, "6 items" for the page's rollup.</param>
    public static string SummariseUnmet(IEnumerable<PlanRequirementRowViewModel> unmet, Func<int, string>? count = null)
    {
        ArgumentNullException.ThrowIfNull(unmet);
        count ??= value => value.ToString("N0", CultureInfo.CurrentCulture);
        var rows = unmet.ToArray();
        var needed = rows.Count(row => row.IsHeldKnown);
        var unknown = rows.Length - needed;
        return (needed, unknown) switch
        {
            (0, 0) => string.Empty,
            (_, 0) => $"{count(needed)} still needed",
            (0, _) => $"{count(unknown)} to check",
            _ => $"{count(needed)} still needed · {count(unknown)} to check",
        };
    }

    private static string HandlingLabel(RequirementHandling handling) => handling switch
    {
        RequirementHandling.Bring => "Bring",
        RequirementHandling.FindInRaid => "Find in raid",
        _ => "Hand in",
    };
}
