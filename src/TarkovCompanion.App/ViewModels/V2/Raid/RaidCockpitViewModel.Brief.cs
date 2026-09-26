using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#712 0-9] The pre-raid brief in the Raid panel, from the situation and this page's own lists.</summary>
/// <remarks>
/// The extract and quest lists are the ones the panel already builds for the map on screen (the
/// side filter of #873, the requirement words of #737/#743, the quests of the map's objectives),
/// so the brief and the cards cannot disagree. The map follows the raid from the log's map line,
/// and the brief drops those lists whenever they were built for a different map.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private SituationService? _briefSituation;
    private ISituationPlaces? _briefPlaces;
    private IMapBossCatalog? _briefBosses;
    private string? _briefBossMap;
    private IReadOnlyList<MapBossChance> _briefBossList = [];
    private bool _briefBossesRead;

    public PreRaidBriefViewModel PreRaidBrief { get; } = new();

    internal void AttachPreRaidBrief(SituationService situation, ISituationPlaces? places, IMapBossCatalog? bosses)
    {
        _briefSituation = situation ?? throw new ArgumentNullException(nameof(situation));
        _briefPlaces = places;
        _briefBosses = bosses;
        situation.Changed += (_, _) => Dispatch(RefreshPreRaidBrief);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapExtracts) or nameof(QuestPanel) or nameof(SelectedMap))
            {
                RefreshPreRaidBrief();
            }
        };
        Dispatch(RefreshPreRaidBrief);
    }

    private void RefreshPreRaidBrief()
    {
        if (_briefSituation is null || _disposed)
        {
            return;
        }

        var situation = _briefSituation.Current;
        if (!AppFeatureFlags.Current.IsOn(Flag.PreRaidBrief) || !PreRaidBriefBuilder.ShowsFor(situation))
        {
            PreRaidBrief.Show(TarkovCompanion.App.ViewModels.V2.Raid.PreRaidBrief.Hidden);
            return;
        }

        var mapId = situation.Map!.Value;
        if (!string.Equals(_briefBossMap, mapId, StringComparison.OrdinalIgnoreCase))
        {
            _briefBossMap = mapId;
            _briefBossList = [];
            _briefBossesRead = false;
            _ = LoadBriefBossesAsync(mapId);
        }

        var listsMap = _map.SelectedLocation?.Id ?? _map.RenderModel?.Location.Id;
        var side = situation.Side?.Value switch
        {
            SituationSide.Pmc => "PMC",
            SituationSide.Scav => "scav",
            _ => null,
        };
        var loot = Renderer?.HighValueLoot is { IsUnavailable: false, Rows.Count: > 0 } layer ? layer.Rows.Count : (int?)null;
        PreRaidBrief.Show(PreRaidBriefBuilder.Build(new PreRaidBriefInputs(situation)
        {
            MapName = _briefPlaces?.MapName(mapId),
            RaidLength = _briefPlaces?.RaidLength(mapId, side),
            Bosses = _briefBossList,
            BossesRead = _briefBossesRead,
            ListsMapId = listsMap,
            Quests = listsMap is null
                ? []
                : [.. QuestPanel.Select(quest => new PreRaidBriefQuest(listsMap, quest.Task, quest.Objectives, quest.Bring, quest.IsPinned))],
            Extracts = [.. MapExtracts.Select(row => new PreRaidBriefExtract(row.Name, row.RequirementText, row.IsTransit))],
            LootSpots = loot,
            LootSpotsCapped = loot >= HighValueLootPageSize,
        }));
    }

    private const int HighValueLootPageSize = TarkovCompanion.App.ViewModels.V2.MapRenderer.HighValueLootLayerViewModel.PageSize;

    private async Task LoadBriefBossesAsync(string mapId)
    {
        if (_briefBosses is null)
        {
            return;
        }

        try
        {
            var read = await _briefBosses.GetAsync(mapId, CancellationToken.None).ConfigureAwait(false);
            Dispatch(() =>
            {
                if (string.Equals(_briefBossMap, mapId, StringComparison.OrdinalIgnoreCase))
                {
                    _briefBossList = read;
                    _briefBossesRead = true;
                    RefreshPreRaidBrief();
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The brief keeps its "no bosses" line; the rest of it does not depend on this read.
        }
    }
}
