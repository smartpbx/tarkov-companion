using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One quest objective in the list beside the plan, numbered like its marker.</summary>
public sealed class RaidObjectiveRowViewModel : BindableViewModel
{
    private bool _isSelected;

    public RaidObjectiveRowViewModel(QuestObjectiveEntry entry, Action<string> select)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(select);
        ObjectiveId = entry.ObjectiveId;
        Number = entry.Number;
        Task = entry.Objective.TaskName;
        Description = entry.Objective.Description;
        IsPlaced = entry.IsPlaced;
        Where = entry.FloorLabel.Length == 0
            ? entry.PlacementLabel
            : $"{entry.PlacementLabel} · {entry.FloorLabel}";
        SelectCommand = new DelegateCommand(() => select(ObjectiveId));
    }

    public string ObjectiveId { get; }

    /// <summary>The number on its marker; empty where it has no marker.</summary>
    public string Number { get; }

    public bool HasNumber => Number.Length > 0;

    public string Task { get; }

    public string Description { get; }

    /// <summary>"Area · 2nd Floor", "One of 5 places", "No location": what the map shows for it, in words.</summary>
    public string Where { get; }

    public bool IsPlaced { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>What a selected quest objective is, on the card beside the plan.</summary>
/// <remarks>
/// Everything here is said by what the catalog and the player's own progress hold; nothing is
/// fetched. The wiki is a link out and no more: the page opens in the player's browser, and none
/// of its text is read, embedded or copied.
/// </remarks>
public sealed class RaidObjectiveDetailViewModel
{
    private readonly Func<string?, bool> _openWiki;
    private readonly string? _wikiUri;

    public RaidObjectiveDetailViewModel(
        QuestObjectiveEntry entry,
        Func<string, string> nameOfItem,
        Func<string?, bool> openWiki,
        Action close)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(nameOfItem);
        ArgumentNullException.ThrowIfNull(openWiki);
        ArgumentNullException.ThrowIfNull(close);
        var objective = entry.Objective;
        ObjectiveId = entry.ObjectiveId;
        Number = entry.Number;
        Task = objective.TaskName;
        Description = objective.Description;
        Where = entry.PlacementLabel;
        Floor = entry.FloorLabel;
        NoLocationReason = entry.NoLocationReason ?? string.Empty;
        Status = StatusOf(objective);
        // The catalog does not name every item a quest asks for (quest-only items are not in it),
        // and an id is no help to anybody reading the card.
        string Named(string itemId) => nameOfItem(itemId) is var name && !string.Equals(name, itemId, StringComparison.Ordinal)
            ? name
            : "item not in the catalog";
        var bring = QuestItemRequirementFormatter.DescribeBring(objective.ItemTargets, Named);
        var handIn = QuestItemRequirementFormatter.DescribeHandIn(objective.ItemTargets, objective.FoundInRaidRequired, Named);
        Items = new[] { bring, handIn }.Where(line => line.Length > 0).ToArray();
        FoundInRaid = objective.ItemTargets.Count == 0 || objective.FoundInRaidRequired is null
            ? string.Empty
            : objective.FoundInRaidRequired == true ? "Found in raid" : "Found in raid not required";
        Remaining = RemainingOf(objective);
        _openWiki = openWiki;
        _wikiUri = objective.WikiUri;
        OpenWikiCommand = new DelegateCommand(() => _openWiki(_wikiUri));
        CloseCommand = new DelegateCommand(close);
    }

    public string ObjectiveId { get; }

    public string Number { get; }

    public bool HasNumber => Number.Length > 0;

    public string Task { get; }

    public string Description { get; }

    /// <summary>"Area", "One of 5 places", "Marked spot" or "No location".</summary>
    public string Where { get; }

    /// <summary>"2nd Floor" where the objective is on a floor above the ground plan; empty otherwise.</summary>
    public string Floor { get; }

    public bool HasFloor => Floor.Length > 0;

    /// <summary>Why it is not on the map, where it is not.</summary>
    public string NoLocationReason { get; }

    public bool HasNoLocationReason => NoLocationReason.Length > 0;

    /// <summary>Whether the player has this quest active, and whether they pinned it.</summary>
    public string Status { get; }

    /// <summary>What to carry and what to hand over, one line each, with names and never ids.</summary>
    public IReadOnlyList<string> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>"Found in raid" where the objective asks for it, and empty where it asks nothing of the kind.</summary>
    public string FoundInRaid { get; }

    public bool HasFoundInRaid => FoundInRaid.Length > 0;

    /// <summary>"2 of 3 still needed" where the objective counts, and empty where it does not.</summary>
    public string Remaining { get; }

    public bool HasRemaining => Remaining.Length > 0;

    /// <summary>Where the link goes, named, because it leaves the app.</summary>
    public string WikiLabel => "Open the wiki page";

    public string WikiAttribution => WikiLinkPolicy.Attribution;

    public bool HasWiki => WikiLinkPolicy.IsAllowed(_wikiUri);

    public ICommand OpenWikiCommand { get; }

    public ICommand CloseCommand { get; }

    private static string StatusOf(QuestMapObjectiveReadModel objective)
    {
        var active = objective.TaskState == RecordedTaskState.Active &&
            objective.ObjectiveState != RecordedObjectiveState.Completed;
        var pinned = objective.IsTaskPinned || objective.IsObjectivePinned;
        return (active ? "Active" : "Not active") + (pinned ? " · Pinned" : string.Empty);
    }

    private static string RemainingOf(QuestMapObjectiveReadModel objective)
    {
        if (objective.TargetCount is not { } target || target <= 0)
        {
            return string.Empty;
        }

        var remaining = Math.Max(0, target - (objective.RecordedCount ?? 0));
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{remaining:0.##} of {target:0.##} still needed");
    }
}
