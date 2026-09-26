using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#902 P3/P4] The Raid page's remembered choices: read at start, written when changed, and read
/// again when Setup's Backup &amp; reset or an import replaces the stored layout.
/// </summary>
/// <remarks>
/// Before this, Show completed, Squad, Route squad, the new-mark scope and Follow floor reset at
/// every launch, and a reset left the Raid map showing the old layers, loot filter and panel
/// until a restart, although Setup said everything was back at its default.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private SynchronizationContext? _storedChoicesContext;
    private bool _layersReplaced;
    private int _objectivesHereBeforeDone;
    private ICommand? _toggleDrawingToolsCommand;
    private ICommand? _openLootCardCommand;

    /// <summary>Reads what is stored and listens for a replacement; called once, from the constructor.</summary>
    private void AttachStoredChoices(SynchronizationContext? context)
    {
        _storedChoicesContext = context;
        ReadStoredToggles();
        SyncMyTrail();
        if (_layout is not null)
        {
            _layout.Replaced += LayoutReplaced;
        }

        if (AppFeatureFlags.Current is FeatureFlagService flags)
        {
            flags.Changed += FeatureFlagsChanged;
        }
    }

    private void DetachStoredChoices()
    {
        if (_layout is not null)
        {
            _layout.Replaced -= LayoutReplaced;
        }

        if (AppFeatureFlags.Current is FeatureFlagService flags)
        {
            flags.Changed -= FeatureFlagsChanged;
        }
    }

    private void ReadStoredToggles()
    {
        _showCompletedObjectives = LayoutToggle.Parse(_layout?.Get(WorkspaceLayoutKeys.RaidShowCompleted), false);
        _showSquadObjectives = LayoutToggle.Parse(_layout?.Get(WorkspaceLayoutKeys.RaidSquadObjectives), true);
        _routeSquadStops = LayoutToggle.Parse(_layout?.Get(WorkspaceLayoutKeys.RaidRouteSquad), false);
        _chosenNewMarkScope = _layout?.Get(WorkspaceLayoutKeys.RaidMarkScope) switch
        {
            "squad" => RaidMarkScope.Squad,
            "private" => RaidMarkScope.Private,
            _ => null,
        };
    }

    private void LayoutReplaced(object? sender, EventArgs e)
    {
        if (_storedChoicesContext is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => ReloadStoredChoices(), null);
            return;
        }

        ReloadStoredChoices();
    }

    /// <summary>Everything the Raid page remembers, read again and shown without a restart.</summary>
    internal void ReloadStoredChoices()
    {
        if (_disposed)
        {
            return;
        }

        _layerVisibility.Reload();
        _layersReplaced = true;
        _lootValueFilter.Reload();
        _lootFilter = _lootValueFilter.Apply(_lootFilter);
        _followZoom.Reload();
        _drawWidth.Reload();
        ReloadLeaveMargin(); // [#712 0-6]
        RaiseDrawingWidth();
        _objectiveRouteMaps = null;
        Cards.Reload();
        RestoreContextPanel();
        ReadStoredToggles();
        _map.ReloadStoredChoices();
        SyncMyTrail();
        foreach (var name in new[]
                 {
                     nameof(ContextPanelWidth), nameof(ShowsContextPanel), nameof(ContextPanelToggleLabel),
                     nameof(CoOpExtractVisibility), nameof(CoOpExtractVisibilityLabel),
                     nameof(FollowZoomLabel), nameof(FollowLabel),
                     nameof(ShowCompletedObjectives), nameof(ShowSquadObjectives), nameof(ShowsSquadObjectiveSummary),
                     nameof(RouteSquadStops), nameof(CanRouteSquadStops),
                     nameof(SpawnRadiusMetres), nameof(SpawnRadiusChoices),
                 })
        {
            OnPropertyChanged(name);
        }

        RaiseNewMarkScope();
        FeedObjectiveRouteStops();
        _rebuildRequest.Request();
    }

    /// <summary>
    /// [#902 P3] My trail is read while its layer is on (the player's stored choice, not what Loot
    /// focus hides for a moment) and forgotten when it is turned off; a map change reads the new
    /// map's (MapViewModel.SelectLocationAsync).
    /// </summary>
    private void SyncMyTrail() => _ = _map.SetShowsVisitedAsync(IsMyTrailOn);

    private bool IsMyTrailOn => Renderer is { IsLootFocused: false } renderer &&
        renderer.Scene.View.Layers.FirstOrDefault(state => state.LayerId == MyTrailLayerId) is { } state
            ? state.IsVisible
            : _layerVisibility.Get(MyTrailLayerId) == true;

    /// <summary>The Nearby spawns row, always there: its objects exist only in the raid's opening window.</summary>
    private static MapSceneLayer NearbySpawnsLayer(MapRenderModel model)
    {
        var index = model.Overlays.ToList().FindIndex(overlay => overlay.Kind == MapOverlayKind.Spawns);
        return new(NearbySpawnsLayerId, RaidText.LayerNearbySpawns, index < 0 ? 3 : index, true);
    }

    // ---------------------------------------------------------------------------------------
    // [#902 P3] View › Drawing tools: the draw-mode flag, where the pencil is.
    // ---------------------------------------------------------------------------------------

    /// <summary>The player's choice for the pencil, which is what the row's tick shows.</summary>
    public bool IsDrawingToolsOn => AppFeatureFlags.Current is FeatureFlagService flags
        ? flags.States.First(state => state.Flag.Key == Flag.DrawMode.Key).IsOn
        : IsDrawAvailable;

    /// <summary>Chosen but not in force until a restart; never, while the flag applies at once.</summary>
    public bool DrawingToolsWaitsForRestart => AppFeatureFlags.Current is FeatureFlagService flags &&
        flags.States.First(state => state.Flag.Key == Flag.DrawMode.Key).WaitsForRestart;

    public ICommand ToggleDrawingToolsCommand => _toggleDrawingToolsCommand ??= new DelegateCommand(() =>
    {
        if (AppFeatureFlags.Current is FeatureFlagService flags)
        {
            flags.Set(Flag.DrawMode, !IsDrawingToolsOn);
        }
    });

    private void FeatureFlagsChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            if (_disposed)
            {
                return;
            }

            if (IsDrawMode && !IsDrawAvailable)
            {
                SetInteractionMode(MapInteractionMode.Navigate);
            }

            OnPropertyChanged(nameof(IsDrawAvailable));
            OnPropertyChanged(nameof(IsDrawingToolsOn));
            OnPropertyChanged(nameof(DrawingToolsWaitsForRestart));
        }

        if (_storedChoicesContext is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => Apply(), null);
            return;
        }

        Apply();
    }

    // ---------------------------------------------------------------------------------------
    // [#902 P4] The loot card is the one place the loot filters are chosen.
    // ---------------------------------------------------------------------------------------

    /// <summary>Opens the loot card (and the side panel, if it was put away): from the map's chip.</summary>
    public ICommand OpenLootCardCommand => _openLootCardCommand ??= new DelegateCommand(() =>
    {
        if (_contextPanelHidden)
        {
            ToggleContextPanel();
        }

        Cards.LootFilters.IsExpanded = true;
    });
}
