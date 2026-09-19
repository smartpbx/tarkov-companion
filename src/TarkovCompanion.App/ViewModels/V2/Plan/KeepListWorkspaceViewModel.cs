using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
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
    public string ReasonSummary => string.Join(", ", Reasons);
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

    private readonly KeepListService _service;
    private IReadOnlyList<KeepListGroupViewModel> _groups = [];
    private string _status = "Loading the keep list…";

    public KeepListWorkspaceViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository,
        IItemFactCatalog factCatalog,
        IQuestReadService questReadService)
    {
        _service = new KeepListService(requirements, profileService, itemRepository, factCatalog, questReadService);
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

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

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var plan = await _service.BuildAsync(cancellationToken).ConfigureAwait(true);
            if (!plan.HasData)
            {
                Groups = [];
                Status = "No keep-list data cached yet.";
                return;
            }

            Groups = Present(plan);
            Status = plan.Entries.Count == 0
                ? "Nothing to keep right now — quests, hideout, and keys are all clear."
                : $"{Count(plan.Entries.Count)} to keep";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Groups = [];
            Status = "Keep-list data isn't available yet.";
            System.Diagnostics.Trace.TraceWarning($"Keep list refresh failed: {exception}");
        }
    }

    private static IReadOnlyList<KeepListGroupViewModel> Present(KeepPlan plan) =>
    [
        .. plan.Entries
            .GroupBy(entry => entry.Group)
            .OrderBy(group => group.Key)
            .Select(group => new KeepListGroupViewModel(
                GroupLabels[group.Key],
                group
                    .Select(ToRow)
                    .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray())),
    ];

    private static KeepListRowViewModel ToRow(KeepEntry entry)
    {
        var reasons = new List<string>();
        reasons.AddRange(entry.QuestNeeds.Select(need => $"{Count(need.Remaining)} for {need.TaskName}"));
        reasons.AddRange(entry.HideoutNeeds.Select(need => $"{Count(need.Required)} for {need.StationName}"));
        if (entry.KeyReason is not null)
        {
            reasons.Add(entry.KeyReason);
        }

        if (entry.IsHighValue)
        {
            reasons.Add("high value");
        }

        return new KeepListRowViewModel(entry.ItemId, entry.Name, entry.Item.Tier, entry.IsHighValue, reasons);
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
