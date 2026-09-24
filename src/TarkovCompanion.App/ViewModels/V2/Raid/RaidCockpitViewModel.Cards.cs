using System.ComponentModel;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>The side panel's collapsible cards: see <see cref="RaidPanelCards"/>.</summary>
public sealed partial class RaidCockpitViewModel
{
    private HighValueLootLayerViewModel? _watchedLoot;

    /// <summary>Which of the side panel's cards are open, remembered between launches.</summary>
    public RaidPanelCards Cards { get; private set; } = new(null);

    /// <summary>What each closed card says in its one line.</summary>
    private static readonly IReadOnlyDictionary<string, string> SummaryOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(GroupPanel)] = nameof(SquadSummary),
        [nameof(MapExtracts)] = nameof(ExtractsSummary),
        [nameof(Marks)] = nameof(MarksSummary),
        [nameof(MarkList)] = nameof(GroupMarksSummary),
        [nameof(QuestPanel)] = nameof(TasksSummary),
        [nameof(SpawnAreas)] = nameof(SpawnAreasSummary),
        [nameof(SpawnPanel)] = nameof(SpawnsSummary),
        [nameof(ExtractPanel)] = nameof(WaysOutSummary),
        [nameof(LootPanel)] = nameof(LootNearbySummary),
    };

    public string SquadSummary => Counted(GroupPanel.Count, RaidText.SquadCount);

    public string ExtractsSummary
    {
        get
        {
            var offered = MapExtracts.Count(row => row.IsOffered);
            var all = Counted(MapExtracts.Count, RaidText.ExtractCount);
            return offered > 0 ? RaidText.OfferedAndAll(offered, all) : all;
        }
    }

    public string MarksSummary => Counted(Marks.Count, RaidText.MarkCount);

    public string GroupMarksSummary => Counted(MarkList.Count, RaidText.WaypointCount);

    public string TasksSummary => Counted(QuestPanel.Count, RaidText.TaskCount);

    public string SpawnAreasSummary => Counted(SpawnAreas.Count, RaidText.AreaCount);

    public string SpawnsSummary => Counted(SpawnPanel.Count, RaidText.SpawnCount);

    public string WaysOutSummary => Counted(ExtractPanel.Count, RaidText.WayOutCount);

    public string LootNearbySummary => Counted(LootPanel.Count, RaidText.ItemCount);

    internal static string Counted(int count, Func<int, string> counted) =>
        count == 0 ? RaidText.None : counted(count);

    /// <summary>
    /// Keeps the closed cards' one-line summaries current, and the Corrections card (its own open
    /// flag, RaidCorrectionsViewModel.IsOpen) remembered through the same store as every other card.
    /// </summary>
    private void AttachCardState()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && SummaryOf.TryGetValue(name, out var summary))
            {
                OnPropertyChanged(summary);
            }
        };
        Corrections.IsOpen = Cards.Corrections.IsExpanded;
        Corrections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RaidCorrectionsViewModel.IsOpen))
            {
                Cards.Corrections.IsExpanded = Corrections.IsOpen;
            }
        };
    }

    /// <summary>An extract picked on the map opens Extract options, so its highlighted row can be seen.</summary>
    private void RevealSelectedExtract()
    {
        if (MapExtracts.Any(row => row.IsSelected))
        {
            Cards.Extracts.Reveal();
        }
    }

    /// <summary>
    /// [Issue 563] With no loot data the filters card opens by itself, so "no loot shows up" has
    /// its reason and a Refresh in view, even though the card starts closed.
    /// </summary>
    private void WatchLootAvailability(MapSceneRendererViewModel? renderer)
    {
        if (_watchedLoot is not null)
        {
            _watchedLoot.PropertyChanged -= LootAvailabilityChanged;
        }

        _watchedLoot = renderer?.HighValueLoot;
        if (_watchedLoot is not null)
        {
            _watchedLoot.PropertyChanged += LootAvailabilityChanged;
            RevealUnavailableLoot();
        }
    }

    private void LootAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HighValueLootLayerViewModel.IsUnavailable))
        {
            RevealUnavailableLoot();
        }
    }

    private void RevealUnavailableLoot()
    {
        if (_watchedLoot?.IsUnavailable == true)
        {
            Cards.LootFilters.Reveal();
        }
    }
}
