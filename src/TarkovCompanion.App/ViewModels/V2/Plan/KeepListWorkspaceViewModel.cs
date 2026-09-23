using TarkovCompanion.App.ViewModels.V2.Shell;
using System.Globalization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One item worth keeping, and every reason the synced data has for saying so.</summary>
public sealed record KeepListRowViewModel(
    string ItemId,
    string Name,
    string Tier,
    bool IsHighValue,
    IReadOnlyList<string> Reasons)
{
    public string ReasonSummary => RecommendationReason.Length > 0
        ? RecommendationReason
        : string.Join(", ", Reasons);

    /// <summary>The #274 engine's compact action and first ordered reason.</summary>
    public string RecommendationVerdict { get; init; } = string.Empty;

    public string RecommendationReason { get; init; } = string.Empty;

    public bool HasRecommendation => RecommendationVerdict.Length > 0;

    /// <summary>What the quests still open ask for in all, and how much of it must be found in raid; empty if no quest asks.</summary>
    public string QuestCountLabel { get; init; } = string.Empty;

    /// <summary>What the hideout levels not yet built ask for, against the whole build; empty if the hideout does not.</summary>
    public string HideoutCountLabel { get; init; } = string.Empty;

    /// <summary>"Held 2", or "Held unknown" where no holding is recorded. Unknown is never written as 0.</summary>
    public string HeldLabel { get; init; } = string.Empty;

    public bool HasQuestCount => QuestCountLabel.Length > 0;

    public bool HasHideoutCount => HideoutCountLabel.Length > 0;

    public bool HasCounts => HasQuestCount || HasHideoutCount;

    /// <summary>The requirement engine's concrete needs behind the compact recommendation.</summary>
    public string LearnReason => Reasons.Count == 0
        ? ReasonSummary
        : $"Keep: {string.Join("; ", Reasons.Take(2))}";
}

public sealed record KeepListGroupViewModel(string Label, IReadOnlyList<KeepListRowViewModel> Items)
{
    public string Heading => $"{Label} ({Items.Count})";
}

/// <summary>
/// V2 rough package 25: db4tarkov calls its "items to keep" list editorial. This one is computed
/// from data the companion already syncs — quest and hideout item requirements, key market prices
/// — plus the profile's own recorded progress. Lives under the Plan route (issue #402) because
/// every input already does: the Plan and Hideout tabs read the same requirement catalog and
/// profile, and "what to keep" is a planning question, not a stash-organising one.
/// </summary>
/// <remarks>
/// The computing is <see cref="KeepListService"/> and <see cref="KeepListPlanner"/> (moved out of
/// here by #307); this only lays out what they return, as groups of rows whose reasons read like
/// "3 for Debut, 2 for Lavatory, high value".
/// </remarks>
public sealed class KeepListWorkspaceViewModel : BindableViewModel
{
    private static readonly IReadOnlyDictionary<KeepGroupKind, string> GroupLabels =
        new Dictionary<KeepGroupKind, string>
        {
            [KeepGroupKind.ActiveQuest] = "Quests you're on",
            [KeepGroupKind.Quest] = "Quests ahead of you",
            [KeepGroupKind.Hideout] = "Hideout upgrades",
            [KeepGroupKind.Key] = "Keys worth keeping",
            [KeepGroupKind.HighValue] = "High value",
        };

    private const int MaximumQuestReasons = 3;

    private readonly KeepListService _service;
    private readonly IItemRecommendationAdvisor? _recommendations;
    private IReadOnlyList<KeepListGroupViewModel> _groups = [];
    private IReadOnlyList<object> _rows = [];
    private string _status = "Loading the keep list…";

    public KeepListWorkspaceViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository,
        IItemFactCatalog factCatalog,
        IQuestReadService questReadService,
        IItemRecommendationAdvisor? recommendations = null,
        LearnModeSetting? learnMode = null)
    {
        LearnMode = learnMode ?? new();
        _service = new KeepListService(requirements, profileService, itemRepository, factCatalog, questReadService);
        _recommendations = recommendations;
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    public LearnModeSetting LearnMode { get; }

    public AsyncDelegateCommand RefreshCommand { get; }

    public IReadOnlyList<KeepListGroupViewModel> Groups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                OnPropertyChanged(nameof(HasGroups));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>
    /// The groups laid end to end, each heading followed by its rows, which is what the page
    /// binds. One flat list is what a virtualising panel can take: the real catalog gives about
    /// 480 rows, and as five nested lists every one of them was built and measured on opening the
    /// page (3.8 s in one interface-thread turn on the dev host; twice past the Windows gallery's
    /// 30 s). Flat, only the rows on screen exist.
    /// </summary>
    public IReadOnlyList<object> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    /// <summary>Shown in the pane when the keep list could not be read, with Retry (#453).</summary>
    public LoadFaultNoticeViewModel LoadFault => _loadFault ??= new(() => RefreshAsync(CancellationToken.None));

    private LoadFaultNoticeViewModel? _loadFault;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            LoadFaultInjection.ThrowIfInjected("keep");
            // Read, planned and worded off the interface thread: none of it touches anything bound.
            var (plan, groups) = await OffInterfaceThread.Run(
                async () =>
                {
                    var built = await _service.BuildAsync(cancellationToken).ConfigureAwait(false);
                    var advice = built.HasData && _recommendations is not null
                        ? await _recommendations.GetAsync(
                            built.Entries.Select(entry => entry.ItemId).ToArray(),
                            cancellationToken).ConfigureAwait(false)
                        : new Dictionary<string, V2ItemRecommendation>(StringComparer.Ordinal);
                    return (built, built.HasData ? Present(built, advice) : []);
                },
                cancellationToken).ConfigureAwait(true);
            LoadFault.Clear();
            if (!plan.HasData)
            {
                Groups = [];
                Rows = [];
                Status = "No keep-list data cached yet.";
                return;
            }

            Groups = groups;
            Rows = [.. groups.SelectMany(group => group.Items.Cast<object>().Prepend(group))];
            Status = plan.Entries.Count == 0
                ? "Nothing to keep right now — quests, hideout, and keys are all clear."
                : $"{Count(plan.Entries.Count)} to keep";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Groups = [];
            Rows = [];
            Status = "Keep-list data isn't available yet.";
            LoadFault.Show("The keep list did not load", "Nothing is lost. Retry reads it again.");
            WorkspaceFault.Record("keep", "refresh", exception);
        }
    }

    private static IReadOnlyList<KeepListGroupViewModel> Present(
        KeepPlan plan,
        IReadOnlyDictionary<string, V2ItemRecommendation> recommendations) =>
    [
        .. plan.Entries
            .GroupBy(entry => entry.Group)
            .OrderBy(group => group.Key)
            .Select(group => new KeepListGroupViewModel(
                GroupLabels[group.Key],
                group
                    .Select(entry => ToRow(entry, recommendations.GetValueOrDefault(entry.ItemId)))
                    .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray())),
    ];

    private static KeepListRowViewModel ToRow(KeepEntry entry, V2ItemRecommendation? recommendation)
    {
        var reasons = new List<string>();
        // The planner puts the quests the player is on first. The MS2000 Marker is asked for by 37
        // quests on the real catalog, and naming them all was six lines nobody reads.
        reasons.AddRange(entry.QuestNeeds
            .Take(MaximumQuestReasons)
            .Select(need => $"{Count(need.Remaining)} for {need.TaskName}{FoundInRaidSuffix(need)}{AnyOfSuffix(need)}"));
        if (entry.QuestNeeds.Count > MaximumQuestReasons)
        {
            reasons.Add($"+{Count(entry.QuestNeeds.Count - MaximumQuestReasons)} more quests");
        }

        reasons.AddRange(entry.HideoutNeeds.Select(need => $"{Count(need.Required)} for {need.StationName}"));
        if (entry.KeyReason is not null)
        {
            reasons.Add(entry.KeyReason);
        }

        if (entry.IsHighValue)
        {
            reasons.Add("high value");
        }

        return new KeepListRowViewModel(entry.ItemId, entry.Name, entry.Item.Tier, entry.IsHighValue, reasons)
        {
            RecommendationVerdict = recommendation?.Verdict ?? string.Empty,
            RecommendationReason = recommendation?.Reason ?? string.Empty,
            QuestCountLabel = QuestCount(entry),
            HideoutCountLabel = HideoutCount(entry),
            HeldLabel = entry.Held is { } held ? $"Held {Count(held)}" : "Held unknown",
        };
    }

    /// <summary>" · any of 5" where other items would do as well, so three is not read as three of each.</summary>
    private static string AnyOfSuffix(KeepQuestNeed need) =>
        need.AnyOf > 1 ? $" · any of {Count(need.AnyOf)}" : string.Empty;

    /// <summary>" (2 found in raid)", " (found in raid)" when all of it must be, or nothing when a purchase would do.</summary>
    private static string FoundInRaidSuffix(KeepQuestNeed need) => need.FoundInRaid switch
    {
        <= 0 => string.Empty,
        var found when found >= need.Remaining => " (found in raid)",
        var found => $" ({Count(found)} found in raid)",
    };

    private static string QuestCount(KeepEntry entry)
    {
        if (entry.QuestNeeds.Count == 0)
        {
            return string.Empty;
        }

        // What the quests the player is on ask for is what to have today; the rest is what not to
        // sell. One figure for both read "Quests 79" on a marker three of which were wanted now.
        var total = entry.QuestRemaining;
        var now = entry.QuestRemainingTracked;
        var head = now > 0 && now < total
            ? $"Quests {Count(now)} now, {Count(total - now)} later"
            : $"Quests {Count(total)}";
        if (entry.QuestTotal > total)
        {
            head += $" of {Count(entry.QuestTotal)} overall";
        }

        var found = entry.QuestFoundInRaid;
        return found <= 0
            ? head
            : found >= total
                ? $"{head} · all found in raid"
                : $"{head} · {Count(found)} found in raid";
    }

    private static string HideoutCount(KeepEntry entry)
    {
        if (entry.HideoutNeeds.Count == 0)
        {
            return string.Empty;
        }

        return entry.HideoutTotalBuild > entry.HideoutRemaining
            ? $"Hideout {Count(entry.HideoutRemaining)} of {Count(entry.HideoutTotalBuild)} for the full build"
            : $"Hideout {Count(entry.HideoutRemaining)}";
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
