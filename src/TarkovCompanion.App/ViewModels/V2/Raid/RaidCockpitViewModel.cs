using TarkovCompanion.App.ViewModels.V2.Shell;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One entry in the manual map picker.</summary>
public sealed class RaidMapPickerItemViewModel(string mapId, string name, Func<string, Task> select) : BindableViewModel
{
    public string MapId { get; } = mapId;

    public string Name { get; } = name;

    public ICommand SelectCommand { get; } = new DelegateCommand(() => _ = select(mapId));
}

/// <summary>
/// One piece of artwork this location publishes, for the chooser over the map.
/// </summary>
/// <remarks>
/// [V2 rough package 39] A row is one piece of artwork, not one catalog variant: a map that
/// publishes both a tile photograph and a drawing offers both as separate rows of the same
/// variant, because that is the choice a player actually has. Only artwork that can draw is
/// offered — upstream lists 2D and 3D variants for most maps with no asset path at all, and a
/// row that cannot draw is a row that leads to a blank plan. Both kinds of choice are remembered
/// per map by V1's own selection service.
/// </remarks>
public sealed class RaidArtworkVariantViewModel(
    string key,
    bool prefersDrawing,
    string name,
    string detail,
    bool isSelected,
    Func<string, bool, Task> select)
{
    public string Key { get; } = key;

    /// <summary>Whether this row is the variant's drawing rather than its photograph.</summary>
    public bool PrefersDrawing { get; } = prefersDrawing;

    public string Name { get; } = name;

    /// <summary>What it is, in a word or two: "Interactive", "Drawing · 4 floors".</summary>
    public string Detail { get; } = detail;

    public bool HasDetail => Detail.Length > 0;

    public bool IsSelected { get; } = isSelected;

    public string AutomationId =>
        $"v2-raid-artwork-{MapRendererToken.From(Key)}-{(PrefersDrawing ? "drawing" : "photo")}";

    public ICommand SelectCommand { get; } = new DelegateCommand(() => _ = select(key, prefersDrawing));
}

/// <summary>One local mark, for the marks list beside the map.</summary>
/// <remarks>
/// Its label is precomputed by <see cref="RaidCockpitViewModel.LabelMarksForMap"/> in the same
/// pass that labels the scene object this mark also became, rather than recomputed here — see
/// that method's remark on why a second pass is the wrong shape for this.
/// </remarks>
public sealed class RaidMarkRowViewModel : BindableViewModel
{
    private readonly Guid _id;
    private readonly Func<Guid, string?, Task> _rename;
    private string _editableName;

    public RaidMarkRowViewModel(RaidMark mark, string label, Func<Guid, string?, Task> rename, Func<Guid, Task> remove)
    {
        _id = mark.Id;
        _rename = rename;
        Kind = mark.Kind;
        Label = label;
        _editableName = mark.State.Label ?? string.Empty;
        RenameCommand = new DelegateCommand(() => _ = _rename(_id, EditableName));
        RemoveCommand = new DelegateCommand(() => _ = remove(_id));
    }

    /// <summary>
    /// V2 rough package 20: a mark the group shared, listed beside our own so every mark drawn on
    /// this map has a visible way to remove it in one place. Removal goes to the relay, which
    /// accepts both waypoints and pings; there is nothing local to rename.
    /// </summary>
    public RaidMarkRowViewModel(long groupId, RaidMarkKind kind, string label, string owner, Func<long, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        _id = Guid.Empty;
        _rename = static (_, _) => Task.CompletedTask;
        Kind = kind;
        Label = label;
        Owner = owner;
        IsGroupMark = true;
        _editableName = string.Empty;
        RenameCommand = new DelegateCommand(static () => { });
        RemoveCommand = new DelegateCommand(() => _ = remove(groupId));
    }

    /// <summary>Whose mark this is, when it came from the group; empty for our own.</summary>
    public string Owner { get; } = string.Empty;

    public bool HasOwner => Owner.Length > 0;

    /// <summary>True for a relay mark: removal goes to the group, and it is never renamed here.</summary>
    public bool IsGroupMark { get; }

    public Guid Id => _id;

    public RaidMarkKind Kind { get; }

    /// <summary>The number, custom name, or "Ping" — whichever the map dot beside this row shows.</summary>
    public string Label { get; }

    public string KindLabel => (Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint") + (IsGroupMark ? " · group" : string.Empty);

    // [Issue 508] So this row can carry the same colour as its pin on the map (see
    // RaidCockpitView.axaml's v2-raid-mark-waypoint/v2-raid-mark-ping), the same way its Label
    // already carries the same text.
    public bool IsWaypoint => Kind == RaidMarkKind.Waypoint;
    public bool IsPing => Kind == RaidMarkKind.Ping;

    /// <summary>A ping is "look here now": it is never told apart from another ping by name.</summary>
    public bool CanRename => Kind == RaidMarkKind.Waypoint && !IsGroupMark;

    /// <summary>The rename box's current text. Committing it empty clears back to the number.</summary>
    public string EditableName
    {
        get => _editableName;
        set => SetProperty(ref _editableName, value);
    }

    public ICommand RenameCommand { get; }

    public ICommand RemoveCommand { get; }
}

/// <summary>
/// V2 rough package 15: one extract (or transit) on the current map for the Raid workspace's
/// "Extract options" list, taken from the canonical scene so it lists the same points the map
/// draws, with the same offer state.
/// </summary>
/// <remarks>
/// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is." <see cref="Id"/>
/// and <see cref="Position"/> are what let a row select and centre its own marker; <see
/// cref="IsSelected"/> is mutable (not carried by the primary constructor, which every rebuild
/// replaces with a fresh instance) so the row can be re-marked after a rebuild without losing the
/// player's own selection — see RaidCockpitViewModel.SyncExtractSelection.
/// </remarks>
public sealed class RaidExtractRowViewModel : BindableViewModel
{
    private readonly MapSceneOfferState _offerState;
    private bool _isSelected;

    public RaidExtractRowViewModel(
        MapSceneObjectId id,
        MapScenePoint position,
        string name,
        string detail,
        MapSceneOfferState offerState,
        ICommand selectCommand,
        MapExtractRequirements? requirements = null,
        string? timeLeft = null)
    {
        Id = id;
        Position = position;
        Name = name;
        Detail = detail;
        _offerState = offerState;
        SelectCommand = selectCommand;
        Requirements = requirements;
        RequirementText = MapExtractRequirementText.Describe(requirements, name, timeLeft);
    }

    /// <summary>The scene object this row is the same extract as, for the map to select.</summary>
    public MapSceneObjectId Id { get; }

    /// <summary>Where its marker is, so selecting the row can centre the map on it too.</summary>
    public MapScenePoint Position { get; }

    public string Name { get; }

    /// <summary>Faction or "Transit": who can use it, in one or two words.</summary>
    public string Detail { get; }

    public MapExtractRequirements? Requirements { get; }

    public string RequirementText { get; private set; }

    public bool HasRequirements => RequirementText.Length > 0;

    public bool NeedsSwitch => Requirements?.RequiresSwitch == true;

    public bool NeedsKey => Requirements?.RequiresKey == true;

    public bool NeedsPayment => Requirements?.RequiresPayment == true;

    public bool NeedsCoOp => Requirements?.RequiresCoOp == true;

    public bool IsOneTime => Requirements?.IsOneTime == true;

    public bool NeedsNoBackpack => Requirements?.RequiresNoBackpack == true;

    public bool NeedsNoArmor => Requirements?.RequiresNoArmor == true;

    public bool NeedsItems => Requirements?.RequiresItems == true;

    public bool HasTimedWindow => Requirements?.HasTimedWindow == true;

    public void UpdateTimeLeft(string? timeLeft)
    {
        var text = MapExtractRequirementText.Describe(Requirements, Name, timeLeft);
        if (RequirementText != text)
        {
            RequirementText = text;
            OnPropertyChanged(nameof(RequirementText));
        }
    }

    public bool IsOffered => _offerState == MapSceneOfferState.Offered;

    public bool IsNotOffered => _offerState == MapSceneOfferState.NotOffered;

    public string OfferLabel => _offerState switch
    {
        MapSceneOfferState.Offered => "Offered",
        MapSceneOfferState.NotOffered => "Not offered",
        _ => "",
    };

    public bool HasOfferLabel => _offerState != MapSceneOfferState.Unknown;

    /// <summary>[Issue 286] "~6–11 min" by the suggested route, where one was planned to this extract.</summary>
    public string Estimate { get; private init; } = string.Empty;

    public bool HasEstimate => Estimate.Length > 0;

    /// <summary>This is the extract whose routes are the ones drawn on the map.</summary>
    public bool IsRouted { get; private init; }

    /// <summary>Draws this extract's routes instead. Null where no route was planned.</summary>
    public ICommand? RouteCommand { get; private init; }

    /// <summary>Selects this extract's own marker on the map and centres the camera on it.</summary>
    public ICommand SelectCommand { get; }

    /// <summary>[Issue 594] This is the extract selected on the map: the row highlights to match.</summary>
    public bool IsSelected => _isSelected;

    /// <summary>The row's own accent border: a planned route, a selection, or both — either says
    /// "this is the one" and neither should read as louder than the other.</summary>
    public bool IsHighlighted => IsRouted || IsSelected;

    public void SetSelected(bool selected)
    {
        if (SetProperty(ref _isSelected, selected, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(IsHighlighted));
        }
    }

    /// <summary>[#453] Whether two lists of rows would show the same thing, row for row.</summary>
    /// <remarks>The commands are not compared: each one acts on the row's own id, name and place.</remarks>
    internal static bool ReadSame(IReadOnlyList<RaidExtractRowViewModel> shown, IReadOnlyList<RaidExtractRowViewModel> next)
    {
        if (shown.Count != next.Count)
        {
            return false;
        }

        for (var index = 0; index < shown.Count; index++)
        {
            var (a, b) = (shown[index], next[index]);
            if (a.Id != b.Id || a.Position != b.Position || a.Name != b.Name || a.Detail != b.Detail ||
                a.RequirementText != b.RequirementText ||
                a._offerState != b._offerState || a.Estimate != b.Estimate || a.IsRouted != b.IsRouted ||
                (a.RouteCommand is null) != (b.RouteCommand is null))
            {
                return false;
            }
        }

        return true;
    }

    internal RaidExtractRowViewModel WithRoute(string estimate, bool isRouted, ICommand command) =>
        new(Id, Position, Name, Detail, _offerState, SelectCommand, Requirements) { Estimate = estimate, IsRouted = isRouted, RouteCommand = command };
}

/// <summary>
/// Hosts the canonical map renderer (<see cref="MapSceneRendererViewModel"/>) as a raid cockpit:
/// map picker, layer toggles the renderer already draws, the raid timer and extract panel, and
/// local marks.
/// </summary>
/// <remarks>
/// This is the adapter <c>MapSceneAssembler</c> was built for. It never draws anything itself —
/// it turns the V1 map's render model plus loot, extract, quest, and mark data into one
/// <see cref="MapSceneSnapshot"/> and lets the renderer draw that. The renderer, the assembler,
/// and the high-value-loot layer are reused unchanged.
/// </remarks>
public sealed partial class RaidCockpitViewModel : BindableViewModel, IDisposable
{
    private static readonly MapSceneLayerId MarksLayerId = new("my-marks");
    // [V2 rough package 22] The three layers the V1 map drew that the cockpit never did. They
    // are ordinary scene layers, so each one gets a switch in the bottom strip for free and a
    // paired device would receive them with the rest of the scene.
    private static readonly MapSceneLayerId PlayerLayerId = new("you");
    private static readonly MapSceneLayerId VisitedLayerId = new("visited");
    private static readonly MapSceneLayerId SquadLayerId = new("squad");
    private static readonly MapSceneLayerId SpawnsLayerId = new("spawns");
    /// <summary>
    /// The plan rectangle to fall back on when the map cannot say where its artwork is.
    /// </summary>
    /// <remarks>
    /// This used to be the plan bounds for every map, and every coordinate in the scene — an
    /// extract, the player's dot, a trail, a loot spawn — is a Leaflet map unit from the
    /// variant's own transform, not a percentage. The two spaces only coincide for a map whose
    /// projected bounds happen to run 0–100, so on a real map the markers were laid out in a
    /// different rectangle from the artwork, the off-plan count counted the mismatch, and the
    /// camera opened centred on a point that was not on the plan at all. The real rectangle now
    /// comes from <see cref="MapPlanProjection"/>; this remains only for a model that has no
    /// usable transform, where nothing can be placed anyway.
    /// </remarks>
    private static readonly MapSceneBounds UnplaceablePlanBounds = new(0, 0, 100, 100);
    private const string MarkIdPrefix = "mark:";
    private const string PlayerObjectId = "you:position";
    private const string PlayerTrailObjectId = "you:trail";
    private const string PlannedReplayObjectId = "you:planned-route";

    /// <summary>The longest edge a composed tile picture is allowed to have, in pixels.</summary>
    private const double MaximumComposedTileExtent = 4096;

    private readonly MapViewModel _map;
    private readonly RaidPageViewModel _raid;
    private readonly IRuntimeStateStore _stateStore;
    private readonly MapSceneAssembler _assembler;
    private readonly IHighValueLootRuntimeSource _lootSource;
    private readonly HistoricalTrafficSource _traffic;
    private readonly IRaidMarkStore _marks;
    private readonly GroupSessionService? _groupSession;
    private readonly TarkovDevMapAssetCache _assetCache;
    private readonly TimeProvider _timeProvider;
    private readonly EarlyRaidSpawnPolicy _earlyRaidSpawns;
    private readonly IWorkspaceLayoutStore? _layout;
    private readonly IRaidHistoryService? _raidHistory;
    private readonly FollowZoomSetting _followZoom;
    private readonly LootValueFilterSetting _lootValueFilter;
    private double _contextPanelWidth = DefaultContextPanelWidth;
    private bool _contextPanelHidden;
    // [Issue 573] Hidden / Dim (default) / Normal, remembered the same way the panel's own width is.
    private CoOpExtractVisibility _coOpExtractVisibility = CoOpExtractVisibility.Dim;
    private readonly MapSceneRendererPresentation _presentation;
    private readonly IWikiLinkOpener? _wikiOpener;

    private long _revision;
    private CancellationTokenSource? _rebuildCancellation;
    private HighValueLootLayerFilterState _lootFilter = HighValueLootLayerFilterState.Default;

    // [Issue 379] The objective whose marker the next right-click on bare map places, and the
    // player's own markers. Optional: a cockpit built without a store offers no placing.
    private string? _armedObjectiveId;
    private readonly IUserQuestMarkStore? _userMarkers;
    // [Issue 571] "Done" by hand: who marked what, and whose profile it is. Optional: a cockpit
    // built without a store offers no Done/Not done and no Show completed.
    private readonly IHandDoneObjectiveStore? _handDone;
    private readonly IPlayerProfileService? _profiles;
    private Guid _activeProfileId;
    private bool _showCompletedObjectives;
    // Letters are decided once per raid and kept — see QuestObjectiveLetterAssignment — so
    // removing an objective, by hand or because the game reported its quest done, never
    // relabels the ones still on the map.
    private readonly QuestObjectiveLetterAssignment _questLetters = new();
    private string _unavailableReason = "Loading the map…";
    private string? _cachedAssetVariantKey;
    private string? _modelFloorId;
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _objectStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
    private bool _followPending;
    private MapSceneBounds _planBounds = UnplaceablePlanBounds;
    private CachedMapAsset? _cachedAsset;
    private string? _backgroundSha;
    private DateTimeOffset _backgroundComposedUtc;
    private Bitmap? _backgroundImage;

    /// <summary>Who is still reading a picture this cockpit made; see <see cref="PictureLeases{TPicture}"/>.</summary>
    private readonly PictureLeases<Bitmap> _pictures;
    private QuestObjectiveScene _questScene = QuestObjectiveScene.Empty;
    private string? _selectedObjectiveId;
    private string _objectiveSignature = string.Empty;
    // [V2 rough package 39] One decoded picture per floor, for the stacked view. Held across
    // rebuilds because a rebuild happens on every raid tick and re-decoding four floors' worth
    // of rasterized plan each time would be the most expensive thing this cockpit does. Cleared
    // when the variant changes, because that is different artwork.
    private readonly Dictionary<string, FloorArtwork> _floorArtwork = new(StringComparer.OrdinalIgnoreCase);
    private string? _floorArtworkVariantKey;
    /// <summary>Why the floors could not be stacked, when the reason is actionable.</summary>
    private string _stackRefusal = string.Empty;
    private readonly PacedDispatch _rebuildRequest;
    private bool _disposed;

    // What the last rebuild was built from. The runtime store publishes for every slice it holds
    // (supervisor, outbox, lifecycle, scan, observation), ten to eighteen times for one screenshot,
    // and only these two are things the plan draws.
    private RaidSnapshot? _seenRaid;
    private GroupSnapshot? _seenGroup;

    // The traffic line, and the bookkeeping that keeps a slow evaluation from overwriting a newer
    // one: the version says which request is current, the time says when the clock last had a
    // reason to move the raid into another phase.
    private HistoricalTrafficView _trafficView = new(
        HistoricalTrafficRuntimeStatus.NoInstalledModel,
        HistoricalTrafficSource.NoModelNotice,
        [],
        null);
    private int _trafficVersion;
    private DateTimeOffset _trafficEvaluatedUtc = DateTimeOffset.MinValue;

    // When the player's marker next crosses from "fresh" to "from an older screenshot". It is the
    // one thing on the plan that changes with the clock alone, so it is the one thing the clock
    // has to be allowed to rebuild for.
    private DateTimeOffset? _positionStaleAtUtc;
    private DateTimeOffset? _spawnWindowExpiresUtc;

    public RaidCockpitViewModel(
        MapViewModel map,
        RaidPageViewModel raid,
        IRuntimeStateStore stateStore,
        MapSceneAssembler assembler,
        IHighValueLootRuntimeSource lootSource,
        // [Issue 311] The installed governed snapshot, evaluated for the map on screen; see
        // TrafficLayerNotice.
        HistoricalTrafficSource traffic,
        IRaidMarkStore marks,
        TarkovDevMapAssetCache assetCache,
        TimeProvider timeProvider,
        // V2 rough package 20: so the marks list can offer "Remove" on a group waypoint or ping
        // too, instead of only on our own. Optional: a cockpit built without one simply lists no
        // group marks, which is what the unit tests and the map gallery want.
        GroupSessionService? groupSession = null,
        // [Package 35] Opens the wiki page of the quest a selected objective belongs to, in the
        // player's browser. Optional: without one the link is simply not offered.
        IWikiLinkOpener? wikiOpener = null,
        // [V2 rough package 46] Remembers how wide he dragged the context panel, and whether he
        // put it away. Optional: without one the panel works and forgets, which is what the unit
        // tests and the map gallery want.
        IWorkspaceLayoutStore? layout = null,
        // [Issue 379] Where the player has put objectives the quest data gives no place for.
        IUserQuestMarkStore? userMarkers = null,
        // [Issue 571] The player's own "done" marks, and whose profile they belong to.
        IHandDoneObjectiveStore? handDone = null,
        IPlayerProfileService? profiles = null,
        // A chosen suggested route belongs to the raid, so Debrief can compare the plan with
        // later screenshot evidence. Optional keeps galleries and isolated map fixtures inert.
        IRaidHistoryService? raidHistory = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _pictures = new(ReleasePicture);
        _raid = raid ?? throw new ArgumentNullException(nameof(raid));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _lootSource = lootSource ?? throw new ArgumentNullException(nameof(lootSource));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _groupSession = groupSession;
        _wikiOpener = wikiOpener;
        _userMarkers = userMarkers;
        _handDone = handDone;
        _profiles = profiles;
        _raidHistory = raidHistory;
        _layout = layout;
        _followZoom = new(layout);
        _lootValueFilter = new(layout);
        _lootFilter = _lootValueFilter.Apply(_lootFilter);
        Cards = new(layout);
        RestoreContextPanel();
        _assetCache = assetCache ?? throw new ArgumentNullException(nameof(assetCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _earlyRaidSpawns = new(_timeProvider);
        _presentation = MapSceneRendererPresentation.English(CultureInfo.CurrentCulture, LocalTime.Zone);
        var synchronizationContext = SynchronizationContext.Current;
        // [#453] Paced and behind input: see PacedDispatch.
        _rebuildRequest = PacedDispatch.ForInterfaceThread(
            synchronizationContext,
            () =>
            {
                if (!_disposed)
                {
                    _ = RebuildAsync();
                }
            });

        RebuildMapPicker();
        RebuildArtworkVariants();

        CancelPlacingObjectiveCommand = new DelegateCommand(() => ArmObjective(null));

        // [V2 rough package 22] Every one of these is V1's own behaviour on V1's own view model.
        // The cockpit owns where the control sits, not what pressing it means.
        ToggleFollowCommand = new DelegateCommand(_map.ToggleFollowPlayer);
        DecreaseFollowZoomCommand = new DelegateCommand(() => ChangeFollowZoom(-1));
        IncreaseFollowZoomCommand = new DelegateCommand(() => ChangeFollowZoom(1));
        RotateCommand = new DelegateCommand(() => _ = _map.RotateAsync());
        ToggleVisitedCommand = new DelegateCommand(() => _ = _map.ToggleVisitedAsync());
        ToggleGroupNamesCommand = new DelegateCommand(() => _ = _map.ToggleGroupNamesAsync());
        ToggleAutoFloorCommand = new DelegateCommand(_map.ToggleAutoFloor);
        ToggleStackCommand = new DelegateCommand(() => _map.IsStacked = !_map.IsStacked);
        ToggleArtworkCommand = new DelegateCommand(() => _ = _map.ToggleArtworkAsync());
        ToggleHideControlsCommand = new DelegateCommand(() => _ = _map.ToggleHideControlsWhenIdleAsync());
        FrameAreaCommand = new DelegateCommand(FrameArea);
        ClearObjectiveCommand = new DelegateCommand(ClearObjectiveSelection);
        // [Issue 571] Brings a done objective back on the map and the list, dimmed with a check.
        ToggleShowCompletedObjectivesCommand = new DelegateCommand(() => ShowCompletedObjectives = !ShowCompletedObjectives);
        // [Issue 573] Hidden -> Dim -> Normal -> Hidden, one press at a time.
        ToggleCoOpExtractVisibilityCommand = new DelegateCommand(CycleCoOpExtractVisibility);
        UseFloorVariantCommand = new DelegateCommand(() => _ = _map.UseFloorVariantAsync());
        // [V2 rough package 46] One press puts the Raid plan column away and gives the map its width.
        ToggleContextPanelCommand = new DelegateCommand(ToggleContextPanel);

        _map.PropertyChanged += MapPropertyChanged;
        _map.PlayerFollowRequested += PlayerFollowRequested;
        _raid.PropertyChanged += RaidPropertyChanged;
        Corrections = new(_raid.Corrections, _timeProvider);
        Corrections.ShowClock(_raid.Clock);
        AttachCardState();
        _raid.Corrections.Changed += CorrectionsChanged;
        _stateStore.Changed += RuntimeStateChanged;
        _marks.Changed += MarksChanged;
        // [#707] Marks placed here, or on a paired tablet, go to the group as well.
        AttachGroupMarks();
        if (_userMarkers is not null)
        {
            _userMarkers.Changed += UserMarkersChanged;
        }

        if (_handDone is not null)
        {
            _handDone.Changed += HandDoneChanged;
        }

        InitializeAsync().Observe("raid", "initialize");
    }

    /// <summary>The canonical map renderer, once a reviewed map asset is available.</summary>
    public MapSceneRendererViewModel? Renderer { get; private set; }

    public bool HasRenderer => Renderer is not null;

    /// <summary>
    /// How wide the Raid plan column is, and whether it is there at all.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] "The right one is too wide, maybe we can make it adjustable or
    /// something. More map is better." It used to be a proportional column with a 420 floor,
    /// which at 1920 wide took 460 pixels the map could have had. It is now a width the player
    /// drags, defaulting to 360 — enough for the marks list and the squad rows at their natural
    /// width — clamped to something that can still be read at one end and cannot eat the map at
    /// the other, put away entirely with one press, and remembered.
    /// </remarks>
    public const double DefaultContextPanelWidth = 360;

    public const double MinimumContextPanelWidth = 260;

    public const double MaximumContextPanelWidth = 720;

    public double ContextPanelWidth
    {
        get => _contextPanelWidth;
        private set
        {
            var clamped = ClampContextPanelWidth(value);
            if (Math.Abs(clamped - _contextPanelWidth) < 0.5)
            {
                return;
            }

            _contextPanelWidth = clamped;
            OnPropertyChanged(nameof(ContextPanelWidth));
        }
    }

    /// <summary>
    /// A width that can still be read at one end and cannot eat the map at the other.
    /// </summary>
    /// <remarks>
    /// The remembered file is the player's own and nothing hostile writes it, but a width of zero
    /// or of a million is a map nobody can see either way, and a hand-edited or truncated file is
    /// the ordinary case. Internal so the clamp itself is tested rather than a copy of it.
    /// </remarks>
    internal static double ClampContextPanelWidth(double width) => Math.Clamp(
        double.IsFinite(width) ? width : DefaultContextPanelWidth,
        MinimumContextPanelWidth,
        MaximumContextPanelWidth);

    public ICommand ToggleContextPanelCommand { get; }

    public bool ShowsContextPanel => !_contextPanelHidden;

    public string ContextPanelToggleLabel => _contextPanelHidden ? "Show the raid plan" : "Hide the raid plan";

    /// <summary>Drags the panel's edge. The width the player sees is the width that is kept.</summary>
    public void ResizeContextPanel(double width)
    {
        ContextPanelWidth = width;
        _layout?.Set(
            WorkspaceLayoutKeys.RaidPanelWidth,
            _contextPanelWidth.ToString("F0", CultureInfo.InvariantCulture));
    }

    public void ToggleContextPanel()
    {
        _contextPanelHidden = !_contextPanelHidden;
        _layout?.Set(WorkspaceLayoutKeys.RaidPanelHidden, _contextPanelHidden ? "true" : "false");
        OnPropertyChanged(nameof(ShowsContextPanel));
        OnPropertyChanged(nameof(ContextPanelToggleLabel));
    }

    private void RestoreContextPanel()
    {
        if (_layout?.Get(WorkspaceLayoutKeys.RaidPanelWidth) is { } width &&
            double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            _contextPanelWidth = ClampContextPanelWidth(parsed);
        }

        _contextPanelHidden = string.Equals(
            _layout?.Get(WorkspaceLayoutKeys.RaidPanelHidden),
            "true",
            StringComparison.Ordinal);
        _coOpExtractVisibility = CoOpExtracts.ParseVisibility(_layout?.Get(WorkspaceLayoutKeys.CoOpExtractVisibility));
    }

    /// <summary>[Issue 573] Hidden / Dim (default) / Normal, cycled from the map's View/Layers menu.</summary>
    public CoOpExtractVisibility CoOpExtractVisibility
    {
        get => _coOpExtractVisibility;
        private set
        {
            if (SetProperty(ref _coOpExtractVisibility, value))
            {
                OnPropertyChanged(nameof(CoOpExtractVisibilityLabel));
                _layout?.Set(WorkspaceLayoutKeys.CoOpExtractVisibility, value.ToString());
                _rebuildRequest.Request();
            }
        }
    }

    /// <summary>"Co-op extracts: Dim" — what the menu button says today, and what pressing it does next.</summary>
    public string CoOpExtractVisibilityLabel => $"Co-op extracts: {_coOpExtractVisibility}";

    public void CycleCoOpExtractVisibility() => CoOpExtractVisibility = _coOpExtractVisibility switch
    {
        CoOpExtractVisibility.Hidden => CoOpExtractVisibility.Dim,
        CoOpExtractVisibility.Dim => CoOpExtractVisibility.Normal,
        _ => CoOpExtractVisibility.Hidden,
    };

    /// <summary>
    /// Shown over the map when a floor's drawing could not be rasterised, with Retry (#452).
    /// </summary>
    /// <remarks>
    /// The rasteriser runs in a child process so that a native fault in it cannot take the
    /// application. What the player saw when it did fail was either every floor drawn at once or
    /// a sentence in a card, and no way to try again short of picking another map and coming
    /// back. Retry forgets the cached answer and rebuilds, which sends the drawing to a fresh
    /// child.
    /// </remarks>
    public LoadFaultNoticeViewModel MapFault => _mapFault ??= new(RetryMapAsync);

    private LoadFaultNoticeViewModel? _mapFault;

    private Task RetryMapAsync()
    {
        _cachedAssetVariantKey = null;
        _cachedAsset = null;
        return RebuildAsync();
    }

    /// <summary>Why the map is not showing, while <see cref="HasRenderer"/> is false.</summary>
    public string UnavailableReason
    {
        get => _unavailableReason;
        private set => SetProperty(ref _unavailableReason, value);
    }

    public IReadOnlyList<RaidMapPickerItemViewModel> MapPicker { get; private set; } = [];
    /// <summary>The picker entry for the map currently shown, for a header selector that stays
    /// showing the current map name rather than resetting to a blank picker each time.</summary>
    /// <remarks>
    /// The map that was chosen, not the map that has finished loading: the render model arrives
    /// with the artwork, and until then the selector in the top bar was blank.
    /// </remarks>
    public RaidMapPickerItemViewModel? SelectedMap =>
        MapPicker.FirstOrDefault(item => item.MapId == (_map.SelectedLocation?.Id ?? _map.RenderModel?.Location.Id));

    public IReadOnlyList<RaidMarkRowViewModel> Marks { get; private set; } = [];

    public bool HasMarks => Marks.Count > 0;

    /// <summary>What the installed historical-traffic snapshot says about this map, in one line.</summary>
    /// <remarks>
    /// It was the fixed words "no installed model yet", because the runtime was registered and
    /// never asked. It is now the runtime's own answer for the map on screen, so it changes when a
    /// snapshot is installed, when the map or raid changes and as the raid clock moves into a new
    /// phase. Every state keeps the word "estimate": it is history, never a live position.
    /// </remarks>
    public string TrafficLayerNotice => _trafficView.HasRows ? _trafficView.Notice : PriorNotice ?? _trafficView.Notice;

    /// <summary>The busiest regions and routes the snapshot names, busiest first, as "Dorms · 100%".</summary>
    /// <remarks>[Issue 286] With no installed snapshot, what the structural prior is built from instead.</remarks>
    public IReadOnlyList<string> TrafficRows => _trafficView.HasRows
        ? [.. _trafficView.Rows.Select(row =>
            string.Create(CultureInfo.CurrentCulture, $"{row.Label} · {Math.Round(row.Relative * 100):0}%"))]
        : PriorRows;

    public bool HasTrafficRows => TrafficRows.Count > 0;

    // ---------------------------------------------------------------------------------------
    // [V2 rough package 22] V1 map parity. Every property below forwards to the one MapViewModel
    // this cockpit already holds; none of it is a second implementation of anything. Where a V1
    // control had no home in the #398/#410 concept it went into the floating group over the map
    // (the view toggles) or the right-hand context panel (the panels).
    // ---------------------------------------------------------------------------------------

    /// <summary>Move the map to the player when a screenshot places them.</summary>
    public ICommand ToggleFollowCommand { get; }

    public ICommand DecreaseFollowZoomCommand { get; }

    public ICommand IncreaseFollowZoomCommand { get; }

    public string FollowZoomLabel => string.Create(CultureInfo.CurrentCulture, $"{_followZoom.Value:0.#}×");

    public string FollowLabel => string.Create(CultureInfo.CurrentCulture, $"Follow {_followZoom.Value:0.#}×");

    /// <summary>Turn the plan a quarter, remembered per map.</summary>
    public ICommand RotateCommand { get; }

    /// <summary>Draw where past raids on this map put you.</summary>
    public ICommand ToggleVisitedCommand { get; }

    /// <summary>Write squadmates' names beside their markers.</summary>
    public ICommand ToggleGroupNamesCommand { get; }

    /// <summary>Change floor to the one the screenshot says you are standing on.</summary>
    public ICommand ToggleAutoFloorCommand { get; }

    /// <summary>Draw the floors one above another instead of one at a time.</summary>
    public ICommand ToggleStackCommand { get; }

    /// <summary>Swap between the drawn map and the photographic one.</summary>
    public ICommand ToggleArtworkCommand { get; }

    /// <summary>Fade the floating controls a few seconds after the pointer leaves the map.</summary>
    public ICommand ToggleHideControlsCommand { get; }

    /// <summary>Put the area the raid is happening in on screen.</summary>
    public ICommand FrameAreaCommand { get; }

    /// <summary>Switch to the artwork of this map that actually has the floors on it (V1's "Floors…").</summary>
    public ICommand UseFloorVariantCommand { get; }

    public bool FollowsPlayer => _map.FollowsPlayer;

    public string RotationLabel => _map.RotationLabel;

    public bool IsRotated => _map.IsRotated;

    public bool ShowsVisited => _map.ShowsVisited;

    public string VisitedLabel => _map.VisitedLabel;

    public bool ShowsGroupNames => _map.ShowsGroupNames;

    public bool AutoSelectsFloor => _map.AutoSelectsFloor;

    public bool IsStacked => _map.IsStacked;

    public bool CanStack => _map.CanStack;

    /// <summary>Whether the map on screen is actually drawing its floors as a stack.</summary>
    /// <remarks>
    /// [V2 rough package 39] The renderer's answer, not V1's: V1's own stack is geometry for
    /// V1's canvas, and this cockpit draws through <see cref="MapSceneRendererViewModel"/>.
    /// </remarks>
    public bool HasFloorStack => Renderer?.HasFloorStack ?? false;

    /// <summary>What the stack did, in one line — the renderer's, while it has one.</summary>
    public string StackStatus => _stackRefusal.Length > 0
        ? _stackRefusal
        : Renderer is { HasStackStatus: true } renderer
            ? renderer.StackStatus
            : _map.StackStatus;

    public bool HasStackStatus => StackStatus.Length > 0;

    /// <summary>Where the floor on screen came from: your screenshot, or your own choice.</summary>
    public string FloorSource => _map.FloorSource;

    /// <summary>Puts that answer on the renderer's own floor ladder, where the floors are chosen.</summary>
    private void PublishFloorSource()
    {
        if (Renderer is { } renderer)
        {
            renderer.FloorSourceNote = _map.FloorSource;
            // The switch beside it: whether the plan follows the floor you are on.
            // It was the bottom strip's "Floors".
            renderer.SetFollowFloor(_map.AutoSelectsFloor, ToggleAutoFloorCommand);
        }
    }

    public bool HasFloorSource => _map.HasFloorSource;

    public bool HasArtworkChoice => _map.HasArtworkChoice;

    public bool PrefersDrawing => _map.PrefersDrawing;

    /// <summary>Every reviewed piece of artwork this map publishes, for the chooser.</summary>
    public IReadOnlyList<RaidArtworkVariantViewModel> ArtworkVariants { get; private set; } = [];

    /// <summary>Only worth a chooser when there is actually something to choose between.</summary>
    public bool HasArtworkVariants => ArtworkVariants.Count > 1;

    /// <summary>
    /// Who made the artwork on screen, and which map and game version it is — ADR 0015's asset
    /// attribution, for whichever variant is showing.
    /// </summary>
    public string ArtworkAttribution => Renderer?.ReviewedAssetLabel ?? string.Empty;

    public bool HasArtworkAttribution => ArtworkAttribution.Length > 0;

    public bool HideControlsWhenIdle => _map.HideControlsWhenIdle;

    public bool CanFrameArea => _map.CanFrameArea;

    public string FrameAreaText => _map.FrameAreaText;

    public string FloorVariantHint => _map.FloorVariantHint;

    public bool HasFloorVariant => _map.HasFloorVariant;

    /// <summary>What the map itself is saying — V1's status line, under the map.</summary>
    public string StatusLine => _map.StatusLine;

    public bool HasStatusLine => !string.IsNullOrWhiteSpace(StatusLine);

    /// <summary>The player's own marker exists, so Follow has something to move to.</summary>
    public bool HasPlayerMarker => _map.HasPlayerMarker;

    public bool HasPlayerTrail => _map.HasPlayerTrail;

    // The V1 panels beside the map, listed unchanged.
    public IReadOnlyList<SpawnPanelViewModel> SpawnPanel => _map.SpawnPanel;

    public bool HasSpawnPanel => _map.HasSpawnPanel;

    public string SpawnPanelDetail => _map.SpawnPanelDetail;

    public bool HasSpawnPanelDetail => !string.IsNullOrWhiteSpace(SpawnPanelDetail);

    public IReadOnlyList<SpawnThreatViewModel> SpawnThreats => _map.SpawnThreats;

    public bool HasSpawnThreats => _map.HasSpawnThreats;

    public IReadOnlyList<ExtractPanelViewModel> ExtractPanel => _map.ExtractPanel;

    public bool HasExtractPanel => _map.HasExtractPanel;

    public IReadOnlyList<QuestPanelViewModel> QuestPanel => _map.QuestPanel;

    public bool HasQuestPanel => _map.HasQuestPanel;

    // ---------------------------------------------------------------------------------------
    // [Package 35] Quest objectives on the plan. The scene objects come from the one builder the
    // Plan workspace uses too, and these are the same objectives as a list: numbered like their
    // markers, with the ones that have no place on the map said to have none.
    // ---------------------------------------------------------------------------------------

    /// <summary>Every objective the map's quests ask for here, the placed ones first and in marker order.</summary>
    public IReadOnlyList<RaidObjectiveRowViewModel> QuestObjectives { get; private set; } = [];

    public bool HasQuestObjectives => QuestObjectives.Count > 0;

    /// <summary>
    /// [Issue 571] Whether a done objective (marked by hand, or already recorded complete) is
    /// still drawn — dimmed, with its check — instead of leaving the map and the list, which is
    /// what happens by default.
    /// </summary>
    public bool ShowCompletedObjectives
    {
        get => _showCompletedObjectives;
        set
        {
            if (SetProperty(ref _showCompletedObjectives, value))
            {
                _rebuildRequest.Request();
            }
        }
    }

    public ICommand ToggleShowCompletedObjectivesCommand { get; }

    /// <summary>[Issue 573] Cycles Co-op extract visibility: Hidden -> Dim -> Normal -> Hidden.</summary>
    public ICommand ToggleCoOpExtractVisibilityCommand { get; }

    /// <summary>"3 on the plan · 2 with no location".</summary>
    public string QuestObjectiveSummary
    {
        get
        {
            var placed = QuestObjectives.Count(row => row.IsPlaced);
            var unplaced = QuestObjectives.Count - placed;
            return unplaced == 0
                ? string.Create(CultureInfo.CurrentCulture, $"{placed:N0} on the plan")
                : string.Create(CultureInfo.CurrentCulture, $"{placed:N0} on the plan · {unplaced:N0} with no location");
        }
    }

    /// <summary>The objective selected on the plan or in the list, and what it is.</summary>
    public RaidObjectiveDetailViewModel? SelectedObjective { get; private set; }

    public bool HasSelectedObjective => SelectedObjective is not null;

    public ICommand ClearObjectiveCommand { get; }

    public IReadOnlyList<LootPanelViewModel> LootPanel => _map.LootPanel;

    public bool HasLootPanel => _map.HasLootPanel;

    public IReadOnlyList<GroupMemberPanelViewModel> GroupPanel => _map.GroupPanel;

    public bool HasGroupPanel => _map.HasGroupPanel;

    /// <summary>The group's waypoints as rows, with V1's clear-all and clear-reached.</summary>
    public IReadOnlyList<MarkListItemViewModel> MarkList => _map.MarkList;

    public bool HasMarkList => _map.HasMarkList;

    public bool HasReachedMarks => _map.HasReachedMarks;

    public ICommand ClearMarksCommand => _map.ClearMarksCommand;

    public ICommand ClearReachedMarksCommand => _map.ClearReachedMarksCommand;

    public ICommand RemoveMarkCommand => _map.RemoveMarkCommand;

    /// <summary>
    /// Whether the next right-click on bare map places an objective's marker rather than a ping.
    /// </summary>
    public bool IsPlacingObjective => _armedObjectiveId is not null;

    public ICommand CancelPlacingObjectiveCommand { get; }

    /// <summary>
    /// Package 29 (parity): V1's replay of a past raid, which Debrief's "Watch on map" opens. The map
    /// draws it already (the cockpit reads the same map view model); this is what steps and closes it.
    /// </summary>
    public RaidReplayViewModel Replay => _raid.Replay;

    public string TimeLeft => _raid.TimeLeft;

    public string TimeLeftDetail => _raid.TimeLeftDetail;

    public IReadOnlyList<ActiveExtractViewModel> Extracts => _raid.Extracts;

    public bool HasExtracts => Extracts.Count > 0;

    public string ExtractsNotMatched => _raid.ExtractsNotMatched;

    public bool HasExtractsNotMatched => !string.IsNullOrWhiteSpace(ExtractsNotMatched);

    public string Transits => _raid.Transits;

    public bool HasTransits => !string.IsNullOrWhiteSpace(Transits);

    /// <summary>The raid clock when one is running, otherwise a quiet "In raid" / "Not in raid"
    /// rather than the legacy page's "Unknown" plus a full sentence.</summary>
    /// <remarks>
    /// The clock is <see cref="RaidPageViewModel.Clock"/>, the same string the top bar shows, not a
    /// second reading of the same raid in another format.
    /// </remarks>
    public string RaidPhaseLabel => _raid.Clock.Length > 0
        ? _raid.Clock
        : _stateStore.Current.Raid.State == RaidLifecycleState.InRaid ? "In raid" : "Not in raid";

    /// <summary>
    /// False while no countdown is running, so the quiet phase label stands alone.
    /// </summary>
    /// <remarks>
    /// The detail says where time left came from (a screenshot, or a count from the start). A clock
    /// that is only counting up has no such claim to qualify.
    /// </remarks>
    public bool HasRaidPhaseDetail =>
        !string.Equals(TimeLeft, "Unknown", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(TimeLeftDetail);

    /// <summary>The current map's name for the context panel header, e.g. "CUSTOMS".</summary>
    public string MapTitle => (SelectedMap?.Name ?? Renderer?.LocationLabel ?? string.Empty).ToUpper(CultureInfo.CurrentCulture);

    /// <summary>"12 ways out (transits included; co-op hidden) · 5 spawn areas" for the context panel header.</summary>
    public string MapSummary => string.Join(
        " · ",
        new[]
        {
            MapExtracts.Count == 0 ? null : $"{MapExtracts.Count} ways out (transits included; co-op hidden)",
            SpawnAreas.Count == 0 ? null : $"{SpawnAreas.Count} {(SpawnAreas.Count == 1 ? "spawn area" : "spawn areas")}",
        }.Where(part => part is not null));

    /// <summary>Extracts and transits on the current map, offered first.</summary>
    public IReadOnlyList<RaidExtractRowViewModel> MapExtracts { get; private set; } = [];

    public bool HasMapExtracts => MapExtracts.Count > 0;

    /// <summary>
    /// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is." Selects its
    /// marker and puts it in the middle of the map, the same way "Place on map" does for a quest
    /// objective. Where a route was already planned to it, pressing the row still draws that
    /// route too — selecting one is not meant to take away what pressing it already did.
    /// </summary>
    private void SelectExtract(MapSceneObjectId id, MapScenePoint point, string name)
    {
        // Extract options lists every floor. Move to the marker's floor before selecting it;
        // otherwise the renderer correctly refuses the hidden object and the row appears dead.
        if (Renderer?.Scene.Objects.FirstOrDefault(item => item.Id == id) is { FloorIds.Count: > 0 } item &&
            Renderer.Scene.View.SelectedFloorId is { } selectedFloor &&
            !item.FloorIds.Contains(selectedFloor, StringComparer.OrdinalIgnoreCase))
        {
            Renderer.SelectFloor(item.FloorIds[0]);
        }

        Renderer?.SelectObject(id);
        Renderer?.FocusOn(point);
        if (MapExtracts.FirstOrDefault(row => row.Id == id)?.HasEstimate == true)
        {
            ChooseRouteExtract(name);
        }
    }

    /// <summary>Re-marks whichever row is the one selected on the map, after a rebuild replaced every row.</summary>
    private void SyncExtractSelection()
    {
        var selected = Renderer?.SelectedObject?.ObjectId;
        foreach (var row in MapExtracts)
        {
            row.SetSelected(selected is { } id && row.Id == id);
        }
    }

    /// <summary>The selected marker is an extract or a transit: the "Selected" card below the list shows it.</summary>
    public bool ShowsSelectedExtract => Renderer?.SelectedObject is { } selected &&
        (selected.IsExtractIcon || selected.IsTransitIcon);

    /// <summary>[Issue 594] The route planner's own estimate for the selected extract, where one was planned.</summary>
    public string SelectedExtractEstimate => Renderer?.SelectedObject is { } selected
        ? MapExtracts.FirstOrDefault(row => row.Id == selected.ObjectId)?.Estimate ?? string.Empty
        : string.Empty;

    public bool HasSelectedExtractEstimate => SelectedExtractEstimate.Length > 0;

    /// <summary>Named spawn areas on the current map (potential spawns, never observed players).</summary>
    public IReadOnlyList<string> SpawnAreas { get; private set; } = [];

    public bool HasSpawnAreas => SpawnAreas.Count > 0;

    /// <summary>
    /// Drops a mark where the gesture landed.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Reported as the ping and waypoint buttons being too much work: a
    /// right-click should drop a ping and shift+right-click a waypoint. So there is no armed
    /// state any more — the gesture carries which mark it means, and the two round buttons that
    /// used to arm one went with it, along with the sentence that explained them.
    ///
    /// The host only calls this for a gesture that hit bare map. Something under the pointer is
    /// a removal instead, and the renderer raises exactly one of the two events per gesture, so
    /// the press that removes a mark can never also place one on top of it.
    /// </remarks>
    public void PlaceMarkAt(MapScenePoint point, RaidMarkKind kind)
    {
        if (_armedObjectiveId is { } objectiveId && _userMarkers is not null && _map.RenderModel is { } objectiveModel)
        {
            // [Issue 379] An objective is waiting for its place, so this gesture is that place and
            // not a ping or a waypoint, whichever modifier it carried. The floor is the one the plan
            // is showing, like every other mark: the player clicked there.
            var objectiveFloor = Renderer?.Scene.View.SelectedFloorId ?? objectiveModel.SelectedFloor?.Id;
            ArmObjective(null);
            _ = _userMarkers.PlaceAsync(objectiveId, objectiveModel.Location.Id, objectiveFloor, point.X, point.Y);
            return;
        }

        if (_map.RenderModel is not { } model)
        {
            return;
        }

        // The floor the mark belongs on is whichever one the V2 renderer is showing, not
        // whatever the V1 map last had selected — the two floor selections are independent.
        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        _ = PlaceMarkAsync(kind, model.Location.Id, floorId, point.X, point.Y);
    }

    /// <summary>Which mark a plan gesture means: a waypoint when it is the secondary one, a ping otherwise.</summary>
    /// <remarks>
    /// One place, because the desktop's Shift and the tablet's long press have to agree, and
    /// because "what does the modifier mean" is the kind of thing that drifts between two hosts.
    /// </remarks>
    public static RaidMarkKind MarkKindFor(bool isSecondary) =>
        isSecondary ? RaidMarkKind.Waypoint : RaidMarkKind.Ping;

    /// <summary>
    /// The host calls this from a right-click that hit something on the plan. A ping or a
    /// waypoint of ours is removed; anything else — an extract, a loot spawn, another map
    /// object entirely — is left alone. A right-click that hit nothing never reaches here at
    /// all (<c>MapSceneRendererView.MarkerRightClicked</c> only fires on a hit), so bare-map
    /// right-click keeps whatever it already did, which is nothing.
    /// </summary>
    public void RemoveMarkAt(MapSceneObjectId objectId)
    {
        if (TryParseMarkId(objectId, out var markId))
        {
            _ = _marks.RemoveAsync(markId);
            return;
        }

        // [#707] A squadmate's ping or waypoint, now drawn here too.
        if (TryRemoveGroupMarkAt(objectId))
        {
            return;
        }

        // [Issue 571] A right-click that hit a quest objective's pin, not one of our own marks:
        // the pin's "right-click menu" is Done/Not done, one gesture same as removing a mark is.
        if (_handDone is not null && TryParseObjectiveId(objectId, out var objectiveId))
        {
            ToggleObjectiveDone(objectiveId);
        }
    }

    /// <summary>Whether a scene object id names one of ours, and which mark it is if so.</summary>
    /// <remarks>Internal for direct coverage of "a right-click that hit an extract, a loot spawn,
    /// or anything else that is not a mark of ours does nothing" — see the unit tests.</remarks>
    internal static bool TryParseMarkId(MapSceneObjectId objectId, out Guid markId)
    {
        markId = Guid.Empty;
        return objectId.Value.StartsWith(MarkIdPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(objectId.Value.AsSpan(MarkIdPrefix.Length), out markId);
    }

    /// <summary>
    /// The objective a quest-layer scene object id names, out of <c>quest:{objectiveId}:…</c> —
    /// QuestObjectiveSceneBuilder's and UserQuestMarkerScene's own ids, the only ones this is ever
    /// asked about (see <see cref="TryParseMarkId"/> for the marks this runs after).
    /// </summary>
    internal static bool TryParseObjectiveId(MapSceneObjectId objectId, out string objectiveId)
    {
        objectiveId = string.Empty;
        var value = objectId.Value;
        if (!value.StartsWith("quest:", StringComparison.Ordinal))
        {
            return false;
        }

        var first = "quest:".Length;
        var second = value.IndexOf(':', first);
        objectiveId = second < 0 ? value[first..] : value[first..second];
        return objectiveId.Length > 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _map.PropertyChanged -= MapPropertyChanged;
        _map.PlayerFollowRequested -= PlayerFollowRequested;
        _raid.PropertyChanged -= RaidPropertyChanged;
        _raid.Corrections.Changed -= CorrectionsChanged;
        _stateStore.Changed -= RuntimeStateChanged;
        _marks.Changed -= MarksChanged;
        DetachGroupMarks();
        if (_userMarkers is not null)
        {
            _userMarkers.Changed -= UserMarkersChanged;
        }
        if (_handDone is not null)
        {
            _handDone.Changed -= HandDoneChanged;
        }
        if (Renderer is { } renderer)
        {
            renderer.ViewChangeRequested -= ViewChangeRequested;
            renderer.CameraMovedByPlayer -= CameraMovedByPlayer;
            renderer.CameraZoomedByPlayer -= CameraZoomedByPlayer;
            renderer.HighValueLootFilterRequested -= HighValueLootFilterRequested;
            renderer.HighValueLootRefreshRequested -= HighValueLootRefreshRequested;
            renderer.PropertyChanged -= RendererPropertyChanged;
        }

        _rebuildCancellation?.Cancel();
        _rebuildCancellation?.Dispose();
        // Retired, not disposed: the tablet publisher may be half way through encoding one of
        // these on a pool thread, and closing the window is no better a moment to free it.
        if (_backgroundImage is { } last)
        {
            _pictures.Retire(last);
        }

        foreach (var plate in _floorArtwork.Values)
        {
            _pictures.Retire(plate.Image);
        }

        _floorArtwork.Clear();
    }

    /// <summary>
    /// A lease on a picture this cockpit made, for reading it away from the interface thread, or
    /// null when the picture has already been replaced and must not be touched.
    /// </summary>
    internal IDisposable? TryReadPicture(Bitmap picture) => _pictures.TryRead(picture);

    /// <summary>
    /// Frees a retired picture once nobody is reading it: after the current render pass while the
    /// cockpit is alive (mirroring MapViewModel.ReleaseLater), at once when it is not.
    /// </summary>
    private void ReleasePicture(Bitmap picture)
    {
        if (_disposed)
        {
            picture.Dispose();
            return;
        }

        Dispatcher.UIThread.Post(picture.Dispose, DispatcherPriority.Background);
    }

    /// <summary>The V2 renderer's asset seam: the artwork for the reviewed background asset it
    /// is about to draw, decoded ahead of time in <see cref="RebuildCoreAsync"/> since this is
    /// called synchronously from the renderer's own present/rebuild pass.</summary>
    private IImage? ResolveBackgroundImage(MapSceneAsset asset) =>
        // [V2 rough package 39] A stacked floor's plate is answered by floor rather than by
        // content hash: two floors of one multi-floor drawing are rasterized from the same
        // upstream file and can carry the same hash, and the stack needs them told apart.
        asset.Kind == MapSceneAssetKind.Floor2D && asset.FloorId is { } floorId
            ? _floorArtwork.TryGetValue(floorId, out var plate) ? plate.Image : null
            : _backgroundSha is { } sha && string.Equals(asset.ContentSha256, sha, StringComparison.OrdinalIgnoreCase)
                ? _backgroundImage
                : null;

    /// <summary>One floor's artwork for the stack: the scene asset, and the decoded picture.</summary>
    private sealed record FloorArtwork(MapSceneAsset Asset, Bitmap Image);

    private void ReleaseFloorArtwork()
    {
        if (_floorArtwork.Count == 0)
        {
            return;
        }

        var previous = _floorArtwork.Values.Select(plate => plate.Image).ToArray();
        _floorArtwork.Clear();
        _floorArtworkVariantKey = null;
        foreach (var image in previous)
        {
            _pictures.Retire(image);
        }
    }

    /// <summary>
    /// Every floor's artwork, as scene assets the renderer can stack.
    /// </summary>
    /// <remarks>
    /// Only for a map being drawn from a multi-floor drawing. A map drawn from tiles publishes
    /// no per-floor SVG layer at all, so there is nothing to stack and the renderer says so
    /// rather than drawing one plate and calling it a stack — the same refusal V1's own stack
    /// reaches, for the same reason.
    ///
    /// A floor whose picture will not load is left out. A gap in the stack is honest; a blank
    /// plate at the right height is a floor that looks empty.
    /// </remarks>
    private async Task<IReadOnlyList<MapSceneAsset>> LoadFloorStackAssetsAsync(
        MapRenderModel model,
        CancellationToken cancellationToken)
    {
        var variant = model.Variant;
        // The plates have to be the drawing. A tile grid and a drawing cover different
        // rectangles of the same ground (see MapPlanProjection), so stacking SVG floors under a
        // tile-drawn map's projection would put every marker beside the artwork rather than on
        // it. Saying which press fixes it beats a stack that quietly produced nothing.
        _stackRefusal = !_map.IsStacked || model.Floors.Count <= 1
            ? string.Empty
            : variant.SvgPath is null
                ? "Stacked floors need a drawn map; this one is a photograph"
                : model.Background?.Kind == MapBackgroundKind.TileTemplate
                    ? "Stacked floors need the drawing — choose it above"
                    : string.Empty;
        if (_stackRefusal.Length > 0 || !_map.IsStacked || model.Floors.Count <= 1)
        {
            ReleaseFloorArtwork();
            return [];
        }

        if (!string.Equals(_floorArtworkVariantKey, variant.Key, StringComparison.Ordinal))
        {
            ReleaseFloorArtwork();
            _floorArtworkVariantKey = variant.Key;
        }

        var assets = new List<MapSceneAsset>(model.Floors.Count);
        foreach (var floor in model.Floors)
        {
            if (_floorArtwork.TryGetValue(floor.Id, out var held))
            {
                assets.Add(held.Asset);
                continue;
            }

            CachedMapAsset? cached;
            try
            {
                cached = (await _assetCache.GetSvgAsync(variant, floor, cancellationToken).ConfigureAwait(true)).Asset;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One floor's artwork is not worth the stack; the rest still draws.
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (cached is not { Availability: not MapAssetAvailability.Unavailable } ||
                await LoadBackgroundImageAsync(cached.RenderPath, cancellationToken).ConfigureAwait(true) is not { } image)
            {
                continue;
            }

            var asset = new MapSceneAsset(
                new($"asset:{model.Location.Id}:{variant.Key}:floor:{floor.Id}"),
                MapSceneAssetKind.Floor2D,
                cached.SourceUri,
                cached.LicenseUri,
                cached.ContentSha256,
                string.IsNullOrWhiteSpace(cached.Author) ? "Tarkov.dev community mapping" : cached.Author,
                variant.Key,
                "current",
                MapSceneAssetReviewStatus.Reviewed,
                cached.RetrievedUtc,
                floorId: floor.Id);
            _pictures.Track(image);
            _floorArtwork[floor.Id] = new(asset, image);
            assets.Add(asset);
        }

        return assets;
    }

    /// <summary>
    /// Swaps in newly decoded artwork, disposing the previous bitmap once the current render
    /// pass has finished with it rather than immediately, mirroring MapViewModel.ReleaseLater.
    /// </summary>
    private void ReplaceBackgroundImage(Bitmap? image)
    {
        var previous = _backgroundImage;
        _backgroundImage = image;
        if (image is not null)
        {
            _pictures.Track(image);
        }

        // Retired rather than disposed after the render pass. The render pass was never the only
        // reader: the tablet publisher PNG-encodes this picture on a pool thread, and a picture
        // freed under the encoder is what killed every launch of 2.0.1278 (see PictureLeases).
        if (previous is not null && !ReferenceEquals(previous, image))
        {
            _pictures.Retire(previous);
        }
    }

    /// <summary>
    /// [V2 rough package 23] V1's loaded tile grid as one picture the renderer can draw.
    /// </summary>
    /// <remarks>
    /// V1 has already planned the grid, fetched the reviewed tiles for the level it chose and
    /// decoded them; this only stitches them onto one surface covering the reviewed map, so the
    /// V2 renderer keeps its one-background-image contract without a second tile pipeline beside
    /// V1's. A tile that never arrived is left undrawn — blank in its own square, with every
    /// other tile still in the right place.
    ///
    /// Scaled down when the grid is enormous (Customs' grid at its sharpest level is several
    /// thousand pixels square): the plan rectangle is the same ground either way, so shrinking
    /// the picture costs detail and nothing else, and it keeps one map inside a sane amount of
    /// video memory. Returns null when there is nothing to draw, or when this process has no
    /// rendering platform to draw with — a unit-test host, for one.
    /// </remarks>
    private async Task<ComposedTileArtwork?> ComposeTileArtworkAsync(MapRenderModel model, CancellationToken cancellationToken)
    {
        var tiles = _map.Tiles.Where(tile => tile.HasArtwork).ToArray();
        if (tiles.Length == 0 ||
            MapPlanProjection.For(model) is not { IsValid: true } grid ||
            MapPlanProjection.Reviewed(model) is not { IsValid: true } reviewed ||
            _map.CanvasWidth <= 0 || _map.CanvasHeight <= 0)
        {
            return null;
        }

        // [V2 rough package 46] The crop that gives the map its own shape back. V1's canvas is
        // the whole tile grid, and the grid is snapped outwards to whole tiles, so it carries a
        // margin of up to one tile on each side that is not the map. Composing the picture over
        // the reviewed rectangle instead of the grid makes the picture's pixels exactly the
        // shape of the map, which is the rectangle everything on it now projects into.
        var pixelsPerUnitX = _map.CanvasWidth / grid.Width;
        var pixelsPerUnitY = _map.CanvasHeight / grid.Height;
        var cropLeft = (reviewed.MinimumX - grid.MinimumX) * pixelsPerUnitX;
        var cropTop = (reviewed.MinimumY - grid.MinimumY) * pixelsPerUnitY;
        var cropWidth = reviewed.Width * pixelsPerUnitX;
        var cropHeight = reviewed.Height * pixelsPerUnitY;
        if (!double.IsFinite(cropLeft) || !double.IsFinite(cropTop) || cropWidth <= 0 || cropHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(1, MaximumComposedTileExtent / Math.Max(cropWidth, cropHeight));
        var width = (int)Math.Round(cropWidth * scale);
        var height = (int)Math.Round(cropHeight * scale);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // The identity of this picture, for the renderer's own change detection: which tiles
        // were drawn, at which level, onto how large a surface. Two rebuilds that composed the
        // same grid must produce the same hash or the renderer re-resolves the artwork on every
        // raid tick; one more tile arriving must produce a different one or it never updates.
        var identity = string.Join(
            "\n",
            tiles.Select(tile => string.Create(
                CultureInfo.InvariantCulture,
                $"{tile.LocalPath}|{tile.Left}|{tile.Top}|{tile.Size}")).Order(StringComparer.Ordinal));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{width}x{height}@{cropLeft:F3},{cropTop:F3}\n{identity}")))).ToLowerInvariant();
        if (string.Equals(sha, _backgroundSha, StringComparison.Ordinal) && _backgroundImage is { } unchanged)
        {
            return new(unchanged, sha);
        }

        // [#678] Drawn on a worker: Customs is about two hundred tiles scaled onto one surface,
        // 160-270 ms that held the interface thread on every visit to the map. Only a new set of
        // tiles gets here; an unchanged one returned above without leaving the thread. The lease
        // keeps the tile cache from disposing a tile while the worker draws it.
        var placements = tiles
            .Select(tile => (
                tile.Image,
                Source: new Rect(0, 0, tile.Image.Size.Width, tile.Image.Size.Height),
                Destination: new Rect(
                    (tile.Left - cropLeft) * scale,
                    (tile.Top - cropTop) * scale,
                    tile.Size * scale,
                    tile.Size * scale)))
            .ToArray();
        RenderTargetBitmap? surface;
        try
        {
            using var lease = MapTileLease.Take();
            surface = await Task.Run(
                () =>
                {
                    var drawn = new RenderTargetBitmap(new PixelSize(width, height));
                    try
                    {
                        using var context = drawn.CreateDrawingContext();
                        foreach (var (image, source, destination) in placements)
                        {
                            context.DrawImage(image, source, destination);
                        }
                    }
                    catch
                    {
                        drawn.Dispose();
                        throw;
                    }

                    return drawn;
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // No rendering platform (a headless unit-test host). The scene still builds; it just
            // has no picture, and the renderer says so rather than drawing the wrong one.
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // A newer rebuild took over while this one drew; it composes its own picture.
            surface.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new(surface, sha);
    }

    private sealed record ComposedTileArtwork(Bitmap Image, string ContentSha256);

    /// <summary>Decodes a cached rasterized-map image file off the UI thread.</summary>
    private static async Task<Bitmap?> LoadBackgroundImageAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return await Task.Run(
                () =>
                {
                    using var stream = File.OpenRead(path);
                    return new Bitmap(stream);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private async Task InitializeAsync()
    {
        await _marks.LoadAsync().ConfigureAwait(true);
        if (_userMarkers is not null)
        {
            await _userMarkers.LoadAsync().ConfigureAwait(true);
        }

        if (_handDone is not null)
        {
            await _handDone.LoadAsync().ConfigureAwait(true);
        }

        if (_profiles is not null)
        {
            _activeProfileId = (await _profiles.GetActiveAsync(CancellationToken.None).ConfigureAwait(true)).Id;
        }

        await RebuildAsync().ConfigureAwait(true);
    }

    /// <summary>Puts the next right-click on bare map on this objective, or stops doing so.</summary>
    private void ArmObjective(string? objectiveId)
    {
        if (_armedObjectiveId == objectiveId)
        {
            return;
        }

        _armedObjectiveId = objectiveId;
        OnPropertyChanged(nameof(IsPlacingObjective));
    }

    private void RemoveObjectiveMarker(string objectiveId)
    {
        if (_userMarkers is not null && _map.RenderModel is { } model)
        {
            _ = _userMarkers.RemoveAsync(objectiveId, model.Location.Id);
        }
    }

    private void UserMarkersChanged() => _rebuildRequest.Request();

    private void HandDoneChanged() => _rebuildRequest.Request();

    /// <summary>
    /// [Issue 571] "Done" and "Not done", from the pin's right-click or the Objectives list: hides
    /// it from the map and the active list at once, or undoes that. Needs the objective's own task
    /// id only to mark it done — a store clears every hand mark of a quest together when the game
    /// reports it failed, and that needs the quest, not just the one objective.
    /// </summary>
    private void ToggleObjectiveDone(string objectiveId)
    {
        if (_handDone is null)
        {
            return;
        }

        if (_handDone.Entries.Any(mark => mark.ProfileId == _activeProfileId && mark.ObjectiveId == objectiveId))
        {
            _ = _handDone.MarkNotDoneAsync(_activeProfileId, objectiveId);
            return;
        }

        if (_questScene.Entries.FirstOrDefault(entry => entry.ObjectiveId == objectiveId) is { } entry)
        {
            _ = _handDone.MarkDoneAsync(_activeProfileId, entry.Objective.TaskId, objectiveId);
        }
    }

    /// <summary>Where a spawn's waypoint goes: its marker's position, on the floor it belongs to.</summary>
    internal readonly record struct SpawnWaypointPlacement(double X, double Y, string? FloorId);

    /// <summary>
    /// The place a waypoint for a loot spawn should be put, or null if its geometry somehow has no point.
    /// </summary>
    /// <remarks>
    /// An area is placed at the middle of its outline, so the waypoint sits inside it rather than
    /// on an edge. On a spawn that names several floors the floor being looked at wins when it is
    /// one of them; otherwise the first named floor does, because a waypoint on the wrong floor
    /// would be drawn on none of the floors the spawn is actually on.
    /// </remarks>
    internal static SpawnWaypointPlacement? PlaceSpawnWaypoint(MapSceneObject item, string? selectedFloorId)
    {
        ArgumentNullException.ThrowIfNull(item);
        var points = item.Geometry.Points;
        if (points.Count == 0)
        {
            return null;
        }

        var floor = item.FloorIds.Count == 0
            ? selectedFloorId
            : item.FloorIds.Contains(selectedFloorId ?? string.Empty, StringComparer.Ordinal)
                ? selectedFloorId
                : item.FloorIds[0];
        return new SpawnWaypointPlacement(points.Average(point => point.X), points.Average(point => point.Y), floor);
    }

    /// <summary>Whether this map already has a waypoint on the spot, so a second click adds nothing.</summary>
    internal static bool HasWaypointAt(
        IEnumerable<RaidMark> marks,
        string mapId,
        SpawnWaypointPlacement place) =>
        marks.Any(mark =>
            mark.Kind == RaidMarkKind.Waypoint &&
            string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mark.State.FloorId, place.FloorId, StringComparison.Ordinal) &&
            Math.Abs(mark.State.X - place.X) < 1e-6 &&
            Math.Abs(mark.State.Y - place.Y) < 1e-6);

    private void LootWaypointRequested(HighValueLootEntry entry)
    {
        if (Renderer is not { } renderer || _map.RenderModel is not { } model || entry.SceneObjectId is not { } id)
        {
            return;
        }

        var item = renderer.Scene.Objects.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null ||
            PlaceSpawnWaypoint(item, renderer.Scene.View.SelectedFloorId) is not { } place ||
            HasWaypointAt(_marks.Marks, model.Location.Id, place))
        {
            return;
        }

        // Its own name, so the marks list says which spawn it was. A waypoint is a note to the
        // player, not a claim that anything is there.
        _ = _marks.AddAsync(RaidMarkKind.Waypoint, model.Location.Id, place.FloorId, place.X, place.Y, entry.Spawn.Label);
    }

    private async Task PlaceMarkAsync(RaidMarkKind kind, string mapId, string? floorId, double x, double y)
    {
        await _marks.AddAsync(kind, mapId, floorId, x, y, label: null).ConfigureAwait(true);
    }

    // internal rather than private: a paired tablet holding the control lease switches the
    // desktop's map the same way the manual picker above does (#407) — see
    // TabletMapSurfacePublisher.OnDesktopWorkspaceRequested.
    internal async Task SelectMapAsync(string mapId)
    {
        await _map.FollowRaidAsync(mapId).ConfigureAwait(true);
    }

    /// <summary>
    /// The map view model's properties that this cockpit either re-publishes or redraws for.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 22] A plain forward: the cockpit shows V1's own state, so when V1 says
    /// it changed, the cockpit says the same. The scene is rebuilt only for the properties that
    /// change what is drawn on the plan; a toggle's own label is not one of them.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> MapEchoes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [nameof(MapViewModel.FollowsPlayer)] = [nameof(FollowsPlayer)],
        [nameof(MapViewModel.RotationDegrees)] = [nameof(RotationLabel), nameof(IsRotated)],
        [nameof(MapViewModel.VisitedLabel)] = [nameof(VisitedLabel), nameof(ShowsVisited)],
        [nameof(MapViewModel.ShowsGroupNames)] = [nameof(ShowsGroupNames)],
        [nameof(MapViewModel.AutoSelectsFloor)] = [nameof(AutoSelectsFloor)],
        [nameof(MapViewModel.IsStacked)] = [nameof(IsStacked), nameof(HasFloorStack)],
        [nameof(MapViewModel.StackStatus)] = [nameof(StackStatus), nameof(HasStackStatus)],
        // [V2 rough package 39] Automatic floor selection now says what it did or why it could not.
        [nameof(MapViewModel.FloorSource)] = [nameof(FloorSource), nameof(HasFloorSource)],
        [nameof(MapViewModel.HasArtworkChoice)] = [nameof(HasArtworkChoice)],
        [nameof(MapViewModel.PrefersDrawing)] = [nameof(PrefersDrawing)],
        [nameof(MapViewModel.HideControlsWhenIdle)] = [nameof(HideControlsWhenIdle)],
        [nameof(MapViewModel.FrameAreaText)] = [nameof(FrameAreaText), nameof(CanFrameArea)],
        [nameof(MapViewModel.FloorVariantHint)] = [nameof(FloorVariantHint), nameof(HasFloorVariant)],
        [nameof(MapViewModel.StatusLine)] = [nameof(StatusLine), nameof(HasStatusLine)],
        [nameof(MapViewModel.Status)] = [nameof(StatusLine), nameof(HasStatusLine)],
        [nameof(MapViewModel.SpawnPanel)] = [nameof(SpawnPanel), nameof(HasSpawnPanel)],
        [nameof(MapViewModel.SpawnPanelDetail)] = [nameof(SpawnPanelDetail), nameof(HasSpawnPanelDetail)],
        [nameof(MapViewModel.SpawnThreats)] = [nameof(SpawnThreats), nameof(HasSpawnThreats)],
        [nameof(MapViewModel.ExtractPanel)] = [nameof(ExtractPanel), nameof(HasExtractPanel)],
        [nameof(MapViewModel.QuestPanel)] = [nameof(QuestPanel), nameof(HasQuestPanel)],
        [nameof(MapViewModel.LootPanel)] = [nameof(LootPanel), nameof(HasLootPanel)],
        [nameof(MapViewModel.GroupPanel)] = [nameof(GroupPanel), nameof(HasGroupPanel)],
        [nameof(MapViewModel.MarkList)] = [nameof(MarkList), nameof(HasMarkList), nameof(HasReachedMarks)],
        [nameof(MapViewModel.Floors)] = [nameof(CanStack)],
        [nameof(MapViewModel.SelectedLocation)] = [nameof(SelectedMap), nameof(MapTitle)],
    };

    /// <summary>The map state that changes what is drawn on the plan, so the scene is rebuilt.</summary>
    private static readonly IReadOnlySet<string> MapRedraws = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MapViewModel.PlayerMarkers),
        nameof(MapViewModel.PlayerTrail),
        nameof(MapViewModel.GroupMarkers),
        nameof(MapViewModel.GroupTrails),
        nameof(MapViewModel.VisitedTrails),
        nameof(MapViewModel.ShowsGroupNames),
        nameof(MapViewModel.SelectedFloor),
        nameof(MapViewModel.Overlays),
        nameof(MapViewModel.QuestSceneProjection),
    };

    private void MapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null)
        {
            return;
        }

        if (MapEchoes.TryGetValue(e.PropertyName, out var echoed))
        {
            foreach (var name in echoed)
            {
                OnPropertyChanged(name);
            }
        }

        if (e.PropertyName is nameof(MapViewModel.Status) && Renderer is null && _map.RenderModel is null)
        {
            UnavailableReason = WaitingForMap();
        }

        if (e.PropertyName is nameof(MapViewModel.FloorSource) or nameof(MapViewModel.AutoSelectsFloor))
        {
            // [V2 rough package 46] Beside the ladder that chooses the floor, not only in the
            // status line at the other end of the card. A map on the wrong floor looks the same
            // as one whose following is broken until the answer is where the choice is made.
            PublishFloorSource();
        }

        if (e.PropertyName is nameof(MapViewModel.RenderModel))
        {
            OnPropertyChanged(nameof(SelectedMap));
            _rebuildRequest.Request();
        }
        else if (e.PropertyName is nameof(MapViewModel.Variants)
            or nameof(MapViewModel.SelectedVariant)
            or nameof(MapViewModel.PrefersDrawing))
        {
            RebuildArtworkVariants();
        }
        else if (e.PropertyName is nameof(MapViewModel.Locations))
        {
            // The catalog loads after construction, so the picker built from an empty list at
            // startup has to be rebuilt once it actually has maps to offer.
            RebuildMapPicker();
        }
        else if (e.PropertyName is nameof(MapViewModel.RotationDegrees))
        {
            // V1 turns its own surface; here the same quarter turn is the scene camera's
            // bearing, so markers and place names stay upright over a turned plan.
            Renderer?.SetBearing(_map.RotationDegrees);
        }
        else if (e.PropertyName is nameof(MapViewModel.PlayerMarkers))
        {
            OnPropertyChanged(nameof(HasPlayerMarker));
            OnPropertyChanged(nameof(HasPlayerTrail));
            _rebuildRequest.Request();
        }
        else if (MapRedraws.Contains(e.PropertyName))
        {
            _rebuildRequest.Request();
        }
    }

    /// <summary>
    /// V1 asks its view to move to the player when a screenshot places them; here the scene
    /// camera does it.
    /// </summary>
    /// <remarks>
    /// Deferred when there is no renderer yet (the first screenshot of a session can easily
    /// arrive before the map asset has finished decoding): the pending follow is honoured by the
    /// next rebuild that produces one, rather than being dropped.
    /// </remarks>
    private void PlayerFollowRequested(object? sender, EventArgs e) => FollowPlayer();

    /// <summary>
    /// A drag the player did themselves stops V1 following them.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Reported as the map snapping back after zooming in and panning.
    /// V1 already does this for its own canvas — panning is a deliberate act and the next
    /// screenshot should not undo it — and nothing said it for the V2 renderer, so every
    /// screenshot pulled the camera back onto the player a beat after the drag. Invisible at the
    /// fitted zoom, where the whole map is on screen either way; at zoom 2 or 3 it is a snap.
    ///
    /// Recoverable the same way it is in V1: Follow and Fit both turn following back on.
    /// </remarks>
    private void CameraMovedByPlayer(object? sender, EventArgs e) => _map.ReportManualPan();

    /// <summary>A wheel or zoom-button press changes Follow's remembered magnification.</summary>
    /// <remarks>
    /// [Issue 663] Zoom used to be reported as a generic manual camera move and switched Follow
    /// off. While following, it now changes the setting and recentres the pointer-relative wheel
    /// move on the player. Outside Follow it retains the old behaviour and remains a manual move.
    /// </remarks>
    private void CameraZoomedByPlayer(object? sender, EventArgs e)
    {
        if (!_map.FollowsPlayer || Renderer is null)
        {
            _map.ReportManualPan();
            return;
        }

        _followZoom.Set(Renderer.CameraZoom);
        OnPropertyChanged(nameof(FollowZoomLabel));
        OnPropertyChanged(nameof(FollowLabel));
        FollowPlayer();
    }

    private void ChangeFollowZoom(int steps)
    {
        _followZoom.ChangeBy(steps);
        OnPropertyChanged(nameof(FollowZoomLabel));
        OnPropertyChanged(nameof(FollowLabel));
        if (_map.FollowsPlayer)
        {
            FollowPlayer();
        }
    }

    private void FollowPlayer()
    {
        if (Renderer is null)
        {
            _followPending = true;
            return;
        }

        if (PlayerPlanPoint() is not { } point)
        {
            return;
        }

        _followPending = false;
        Renderer.ShowCamera(point, _followZoom.Value);
    }

    /// <summary>Where the player is in plan coordinates, when a screenshot has placed them.</summary>
    private MapScenePoint? PlayerPlanPoint() =>
        _map.PlayerPosition is { } position && _map.RenderModel is { } model &&
        model.TryMapPosition(position.Position, out var point) &&
        double.IsFinite(point.X) && double.IsFinite(point.Y)
            ? new MapScenePoint(point.X, point.Y)
            : null;

    private static MapFeatureFaction RaidSide(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "pmc" => MapFeatureFaction.Pmc,
        "scav" => MapFeatureFaction.Scav,
        _ => MapFeatureFaction.Unknown,
    };

    private static bool IsNearbySpawn(
        MapOverlayElement element,
        MapRenderModel model,
        IReadOnlyList<NearbySpawn> nearbyAreas) =>
        nearbyAreas.Any(area =>
            model.TryMapPosition(area.Position, out var point) &&
            Math.Abs(point.X - element.Position.X) < 0.001 &&
            Math.Abs(point.Y - element.Position.Y) < 0.001);

    /// <summary>
    /// V1's "Frame area": put the part of the map the raid is actually happening in on screen.
    /// </summary>
    /// <remarks>
    /// V1 frames a rectangle by scrolling and scaling its viewport. This renderer's camera is a
    /// centre and a zoom rather than a rectangle, so the rough equivalent is to centre on the
    /// player at the follow zoom, which is the same question ("show me where this is happening")
    /// with the same answer in the common case. Framing the true bounds is listed as deferred.
    /// </remarks>
    private void FrameArea()
    {
        _map.RequestFit();
        FollowPlayer();
    }

    /// <summary>
    /// Rebuilds the artwork chooser from the location's reviewed variants.
    /// </summary>
    /// <remarks>
    /// <see cref="MapViewModel.Variants"/> is already filtered to variants with a runtime asset,
    /// which is the rule that keeps a variant that cannot draw from being offered. A tile-only
    /// variant is offered and does draw: the cockpit composes V1's loaded tile grid into one
    /// picture (package 23), so "photo" is a real choice rather than a blank plan.
    /// </remarks>
    private void RebuildArtworkVariants()
    {
        ArtworkVariants = BuildArtworkVariants(
            _map.Variants,
            _map.SelectedVariant?.Key,
            _map.PrefersDrawing,
            SelectArtworkAsync);
        OnPropertyChanged(nameof(ArtworkVariants));
        OnPropertyChanged(nameof(HasArtworkVariants));
        OnPropertyChanged(nameof(ArtworkAttribution));
        OnPropertyChanged(nameof(HasArtworkAttribution));
    }

    /// <summary>Internal for direct coverage: the chooser's rows, without standing up a map.</summary>
    internal static IReadOnlyList<RaidArtworkVariantViewModel> BuildArtworkVariants(
        IReadOnlyList<MapVariant> variants,
        string? selectedKey,
        bool prefersDrawing,
        Func<string, bool, Task> select) =>
        [.. variants
            // A variant that cannot draw is never offered. MapViewModel.Variants already filters
            // to those with a runtime asset; this repeats the rule so the chooser is correct on
            // its own terms rather than by someone else's filtering.
            .Where(variant => variant.HasRuntimeAsset)
            // The interactive one first: it is the variant that also carries a transform, so it
            // is the one that can put you and your squad on the picture.
            .OrderByDescending(variant => variant.IsInteractive)
            .ThenBy(variant => variant.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(variant => ArtworkRows(variant, selectedKey, prefersDrawing, select))];

    /// <summary>The rows one variant contributes: two when it publishes both kinds of picture.</summary>
    private static IEnumerable<RaidArtworkVariantViewModel> ArtworkRows(
        MapVariant variant,
        string? selectedKey,
        bool prefersDrawing,
        Func<string, bool, Task> select)
    {
        var isSelectedVariant = string.Equals(variant.Key, selectedKey, StringComparison.OrdinalIgnoreCase);
        if (variant.SvgPath is not null && variant.TilePath is not null)
        {
            // Both, so the choice is which picture rather than which variant — and it is the one
            // choice nearly every map actually offers, because upstream publishes an asset path
            // for the interactive variant alone.
            yield return new(
                variant.Key,
                true,
                "Drawing",
                DescribeVariant(variant),
                isSelectedVariant && prefersDrawing,
                select);
            yield return new(
                variant.Key,
                false,
                "Photo",
                DescribeVariant(variant),
                isSelectedVariant && !prefersDrawing,
                select);
            yield break;
        }

        yield return new(
            variant.Key,
            variant.SvgPath is not null,
            variant.DisplayName,
            DescribeVariant(variant),
            isSelectedVariant,
            select);
    }

    /// <summary>What sort of picture a variant is, in the fewest words that tell them apart.</summary>
    private static string DescribeVariant(MapVariant variant)
    {
        var kind = variant.SvgPath is not null && variant.TilePath is not null
            ? variant.DisplayName
            : variant.SvgPath is not null
                ? "Drawing"
                : "Photo";
        return variant.Floors.Count > 1
            ? string.Create(CultureInfo.CurrentCulture, $"{kind} · {variant.Floors.Count} floors")
            : kind;
    }

    private async Task SelectArtworkAsync(string variantKey, bool prefersDrawing)
    {
        if (_map.Variants.FirstOrDefault(variant =>
                string.Equals(variant.Key, variantKey, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
        {
            return;
        }

        if (!string.Equals(_map.SelectedVariant?.Key, variantKey, StringComparison.OrdinalIgnoreCase))
        {
            // V1's own selection, so the answer is persisted per map exactly as the V1 page
            // persists it and both shells reopen on the artwork you last chose.
            await _map.SelectVariantAsync(chosen).ConfigureAwait(true);
        }

        // Then the picture, for a variant that publishes both. Also V1's, and also remembered.
        if (_map.HasArtworkChoice && _map.PrefersDrawing != prefersDrawing)
        {
            await _map.ToggleArtworkAsync().ConfigureAwait(true);
        }
    }

    private void RebuildMapPicker()
    {
        MapPicker = [.. _map.Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .Select(location => new RaidMapPickerItemViewModel(location.Id, location.Name, SelectMapAsync))];
        OnPropertyChanged(nameof(MapPicker));
        OnPropertyChanged(nameof(SelectedMap));
    }

    private void RaidPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RaidPageViewModel.Clock):
                OnPropertyChanged(nameof(RaidPhaseLabel));
                OnPropertyChanged(nameof(ExtractClockSummary));
                Corrections.ShowClock(_raid.Clock);
                break;
            case nameof(RaidPageViewModel.TimeLeft):
                OnPropertyChanged(nameof(TimeLeft));
                OnPropertyChanged(nameof(RaidPhaseLabel));
                OnPropertyChanged(nameof(HasRaidPhaseDetail));
                OnPropertyChanged(nameof(ExtractClockSummary));
                foreach (var row in MapExtracts)
                {
                    row.UpdateTimeLeft(_raid.TimeLeft);
                }
                // The raid clock ticks once a second, which is the only clock this page has. The
                // plan changes with it exactly once per screenshot: when the marker turns from
                // fresh to "from an older screenshot".
                if (_positionStaleAtUtc is { } staleAt && _timeProvider.GetUtcNow() >= staleAt)
                {
                    _positionStaleAtUtc = null;
                    _rebuildRequest.Request();
                }

                if (_spawnWindowExpiresUtc is { } spawnExpiry && _timeProvider.GetUtcNow() >= spawnExpiry)
                {
                    _spawnWindowExpiresUtc = null;
                    _rebuildRequest.Request();
                }

                // The raid moves from early to mid to late on the clock alone, so the traffic
                // line has to be asked again as it does, without a rebuild for every tick.
                if (_timeProvider.GetUtcNow() - _trafficEvaluatedUtc >= TrafficRefreshInterval)
                {
                    RefreshTraffic();
                }

                break;
            case nameof(RaidPageViewModel.TimeLeftDetail):
                OnPropertyChanged(nameof(TimeLeftDetail));
                OnPropertyChanged(nameof(HasRaidPhaseDetail));
                OnPropertyChanged(nameof(ExtractClockSummary));
                break;
            case nameof(RaidPageViewModel.Extracts):
                OnPropertyChanged(nameof(Extracts));
                OnPropertyChanged(nameof(HasExtracts));
                break;
            case nameof(RaidPageViewModel.ExtractsNotMatched):
                OnPropertyChanged(nameof(ExtractsNotMatched));
                OnPropertyChanged(nameof(HasExtractsNotMatched));
                break;
            case nameof(RaidPageViewModel.Transits):
                OnPropertyChanged(nameof(Transits));
                OnPropertyChanged(nameof(HasTransits));
                break;
        }
    }

    /// <summary>
    /// Rebuilds the plan when the raid or the group changed, and only then.
    /// </summary>
    /// <remarks>
    /// This used to rebuild the whole scene on every publication of the runtime store, whatever
    /// had changed in it. A single screenshot publishes the raid, the screenshot's name, the
    /// outbox's queue, the supervisor's operations and more, each one a full rebuild and each one
    /// recreating every marker on the plan. The store keeps the instance of a slice that an
    /// update did not touch, so a reference comparison is enough to tell them apart.
    /// </remarks>
    private void RuntimeStateChanged(object? sender, EventArgs e)
    {
        var snapshot = _stateStore.Current;
        var raid = snapshot.Raid;
        var group = snapshot.Group;
        if (ReferenceEquals(raid, _seenRaid) && ReferenceEquals(group, _seenGroup))
        {
            return;
        }

        // [Issue 571] A letter belongs to one raid; a genuinely new one starts the alphabet over
        // rather than carrying the last raid's assignments into this one's first rebuild.
        if (raid?.RaidId != _seenRaid?.RaidId)
        {
            _questLetters.Reset();
        }

        _seenRaid = raid;
        _seenGroup = group;
        OnPropertyChanged(nameof(RaidPhaseLabel));
        OnPropertyChanged(nameof(ExtractClockSummary));
        _rebuildRequest.Request();
    }

    /// <summary>The Raid plan's Corrections card: side, clock and offered exits, read or set by hand (#286).</summary>
    public RaidCorrectionsViewModel Corrections { get; }

    private void CorrectionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(RaidPhaseLabel));
        OnPropertyChanged(nameof(ExtractClockSummary));
        _rebuildRequest.Request();
    }

    private void MarksChanged()
    {
        RefreshMarkRows();
        _rebuildRequest.Request();
    }

    private void RefreshMarkRows()
    {
        var mapId = _map.RenderModel?.Location.Id;
        Marks = mapId is null
            ? []
            : [
                .. LabelMarksForMap(_marks.Marks, mapId)
                    .OrderByDescending(item => item.Mark.CreatedUtc)
                    .Select(item => new RaidMarkRowViewModel(item.Mark, item.Label, RenameMarkAsync, id => _marks.RemoveAsync(id))),
                .. GroupMarkRows(mapId),
            ];
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
    }

    /// <summary>
    /// V2 rough package 20: the group's marks for this map, each with the relay removal the Team
    /// workspace already uses, so "how do I get rid of this waypoint" has one answer wherever the
    /// waypoint came from. Empty when this cockpit was built without a group session.
    /// </summary>
    private IEnumerable<RaidMarkRowViewModel> GroupMarkRows(string mapId)
    {
        if (_groupSession is not { } session)
        {
            yield break;
        }

        var group = _stateStore.Current.Group;
        var number = 0;
        foreach (var waypoint in group.Waypoints.Where(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase) && !IsOwnForwardedMark(item.Id)))
        {
            number++;
            yield return new(
                waypoint.Id,
                RaidMarkKind.Waypoint,
                string.IsNullOrWhiteSpace(waypoint.Label) ? number.ToString(CultureInfo.CurrentCulture) : waypoint.Label!,
                waypoint.By,
                id => session.RemoveMarkAsync(id, CancellationToken.None));
        }

        foreach (var ping in group.Pings.Where(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase) && !IsOwnForwardedMark(item.Id)))
        {
            yield return new(
                ping.Id,
                RaidMarkKind.Ping,
                string.IsNullOrWhiteSpace(ping.Label) ? "Ping" : ping.Label!,
                ping.By,
                id => session.RemoveMarkAsync(id, CancellationToken.None));
        }
    }

    private Task RenameMarkAsync(Guid id, string? name) => _marks.RenameAsync(id, name);

    private static readonly TimeSpan TrafficRefreshInterval = TimeSpan.FromSeconds(30);

    private void RefreshTraffic()
    {
        var version = Interlocked.Increment(ref _trafficVersion);
        _trafficEvaluatedUtc = _timeProvider.GetUtcNow();
        RefreshTrafficAsync(_map.RenderModel?.Location.Id, _stateStore.Current.Raid, version).Observe("raid", "refresh traffic");
    }

    private async Task RefreshTrafficAsync(string? mapId, RaidSnapshot raid, int version)
    {
        HistoricalTrafficView view;
        try
        {
            view = await _traffic.EvaluateAsync(mapId, raid, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing the snapshot store or the model can throw is a reason to lose the plan; the
            // line falls back to the same words a machine with no snapshot shows.
            view = new HistoricalTrafficView(
                HistoricalTrafficRuntimeStatus.NoInstalledModel,
                HistoricalTrafficSource.NoModelNotice,
                [],
                null);
        }

        if (_disposed || version != Volatile.Read(ref _trafficVersion) ||
            (view.Notice == _trafficView.Notice && view.Rows.SequenceEqual(_trafficView.Rows)))
        {
            return;
        }

        _trafficView = view;
        OnPropertyChanged(nameof(TrafficLayerNotice));
        OnPropertyChanged(nameof(TrafficRows));
        OnPropertyChanged(nameof(HasTrafficRows));
    }

    private async Task RebuildAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _rebuildCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = cancellation.Token;

        try
        {
            await RebuildCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RebuildCoreAsync(CancellationToken cancellationToken)
    {
        // [#453] Each stage names itself, so a hang record says which one held the interface.
        UiActivity.Step("raid:rebuild");
        RefreshMarkRows();
        NotifySquadWatch();
        RefreshTraffic();
        // Read once, before anything is built from it: a publication that lands while this runs
        // then differs from what was seen and asks for one more pass, instead of being taken for
        // something this pass already drew.
        var runtime = _stateStore.Current;
        _seenRaid = runtime.Raid;
        _seenGroup = runtime.Group;
        var model = _map.RenderModel;
        if (model is null)
        {
            SetUnavailable(WaitingForMap());
            return;
        }

        var variant = model.Variant;
        var selectedFloor = model.SelectedFloor;
        var nowUtc = _timeProvider.GetUtcNow();
        // [Issue 563] The identity the loot publication was projected under, not the bare
        // variant key: with the key, every snapshot was refused as "Transform mismatch".
        var transformVersion = LootSpawnTransformIdentity.For(model.Location.Id, variant);

        // [V2 rough package 23] Two kinds of artwork, one background asset. A map drawn from PNG
        // tiles (Customs is, by default) has no single picture to hand the renderer, so V1's own
        // loaded tiles — the same grid, the same reviewed assets, the same per-floor selection —
        // are composed into one image covering the tile grid exactly. Missing tiles are simply
        // not drawn, which leaves them blank without moving anything else. Before this, the
        // cockpit rasterized the variant's SVG whatever V1 was showing, so a tiles map was drawn
        // from a floor layer covering a fraction of the plan while its status line reported
        // tiles it was not drawing.
        MapSceneAsset asset;
        if (model.Background?.Kind == MapBackgroundKind.TileTemplate)
        {
            if (await ComposeTileArtworkAsync(model, cancellationToken).ConfigureAwait(true) is not { } composed)
            {
                // Still arriving, not missing. V1 empties its tiles the moment another map is
                // chosen and says "unavailable" only when a load has finished with none, so an
                // empty grid that is not unavailable is a load in progress. Tearing the plan down
                // for it put a black box with a sentence in it on screen for the whole load (54 s
                // on a first visit to Reserve, measured 2026-09-20); what is on screen stays until
                // the first tiles of the new map replace it, which is a fraction of a second.
                if (Renderer is not null && _map.Tiles.Count == 0 &&
                    model.Background.Availability != MapAssetAvailability.Unavailable)
                {
                    return;
                }

                ReplaceBackgroundImage(null);
                _cachedAssetVariantKey = null;
                _cachedAsset = null;
                _backgroundSha = null;
                SetUnavailable(model.Background.Message ?? "The map's tiles are not available yet.");
                return;
            }

            // Tiles, so no floor was rasterised and there is nothing left to say about one.
            MapFault.Clear();
            _cachedAssetVariantKey = null;
            _cachedAsset = null;
            // The asset is reviewed when its picture was made, not each time the scene is built.
            // A new timestamp per rebuild made every scene's asset list differ from the last, so
            // the renderer re-resolved the artwork on every raid tick.
            if (!string.Equals(_backgroundSha, composed.ContentSha256, StringComparison.Ordinal))
            {
                _backgroundComposedUtc = nowUtc;
            }

            _backgroundSha = composed.ContentSha256;
            ReplaceBackgroundImage(composed.Image);
            asset = new MapSceneAsset(
                new($"asset:{model.Location.Id}:{variant.Key}:tiles:{selectedFloor?.Id ?? "base"}"),
                MapSceneAssetKind.Background2D,
                model.Background.SourceUri,
                model.LicenseUri,
                composed.ContentSha256,
                string.IsNullOrWhiteSpace(variant.Author) ? "Tarkov.dev community mapping" : variant.Author,
                variant.Key,
                "current",
                MapSceneAssetReviewStatus.Reviewed,
                _backgroundComposedUtc);
        }
        else
        {
            if (variant.SvgPath is null)
            {
                SetUnavailable("This map has no reviewed 2D plan yet, so the V2 renderer cannot draw it.");
                return;
            }

            // Refetching on every rebuild (a runtime snapshot changes often) would re-hash a
            // multi-megabyte SVG on disk each time for no reason once the variant and floor have
            // not changed. Keyed on the floor too: a multi-floor SVG rasterizes a different upstream
            // layer per floor onto the same cache file, so the source hash alone cannot tell floors
            // apart the way V1's own floor switch relies on (see TarkovDevMapAssetCache.GetSvgAsync).
            var assetCacheKey = $"{variant.Key}::{selectedFloor?.Id ?? string.Empty}";
            if (_cachedAssetVariantKey != assetCacheKey || _cachedAsset is null)
            {
                // Before the call, because the call is what died on 2026-09-19: rasterising a
                // drawing faults natively, raising no managed exception for any handler to see.
                CrashBreadcrumbs.Drop("map-asset", $"reading svg {assetCacheKey}");
                // [#452] "map-floor" is the render tool's way of seeing what a floor that would not
                // rasterise looks like, without needing a drawing that really kills the rasteriser.
                var assetResult = LoadFaultInjection.IsInjected("map-floor")
                    ? new MapAssetCacheResult(null, "The map rasteriser failed (crashed with an access violation, 0xc0000005).") { FloorNotDrawn = true }
                    : await _assetCache.GetSvgAsync(variant, selectedFloor, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (assetResult.Asset is not { Availability: not MapAssetAvailability.Unavailable } fetched)
                {
                    _cachedAssetVariantKey = null;
                    _cachedAsset = null;
                    _backgroundSha = null;
                    ReplaceBackgroundImage(null);
                    SetUnavailable(assetResult.Message ?? "The reviewed map asset is not available yet.");
                    if (assetResult.FloorNotDrawn)
                    {
                        MapFault.Show("Could not draw this floor", "The app is fine. Retry draws it again.");
                    }

                    return;
                }

                if (assetResult.FloorNotDrawn)
                {
                    // The cache handed back the whole drawing instead. Say so: every floor at once
                    // with no explanation reads as a broken map.
                    MapFault.Show("Could not draw this floor", "Every floor is shown instead. Retry draws it again.");
                }
                else
                {
                    MapFault.Clear();
                }

                // Decoded, and the cache markers updated, only once both steps succeed: if the
                // decode is cancelled by a newer rebuild landing first, leaving the markers behind
                // would make the next rebuild trust a bitmap that was never actually produced.
                var decoded = await LoadBackgroundImageAsync(fetched.RenderPath, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                // The other half of "reading svg": without it the next launch cannot tell a run
                // that died in the rasteriser from one that died an hour after the map drew.
                CrashBreadcrumbs.Drop("map-asset", $"drew {assetCacheKey}");
                _cachedAssetVariantKey = assetCacheKey;
                _cachedAsset = fetched;
                _backgroundSha = fetched.ContentSha256;
                ReplaceBackgroundImage(decoded);
            }

            var cached = _cachedAsset!;
            // The floor is part of the asset identity, not just its cache key: a multi-floor SVG
            // rasterizes a different upstream layer per floor onto the one cached file, so two
            // floors' artwork share every other field (source, licence, content hash) and would
            // otherwise look identical to the renderer's own change detection (see
            // MapSceneRendererViewModel.ResolveBackground), leaving a stale floor's picture on
            // screen after switching.
            asset = new MapSceneAsset(
                new($"asset:{model.Location.Id}:{variant.Key}:{selectedFloor?.Id ?? "base"}"),
                MapSceneAssetKind.Background2D,
                cached.SourceUri,
                cached.LicenseUri,
                cached.ContentSha256,
                string.IsNullOrWhiteSpace(cached.Author) ? "Tarkov.dev community mapping" : cached.Author,
                variant.Key,
                "current",
                MapSceneAssetReviewStatus.Reviewed,
                cached.RetrievedUtc);
        }

        // What the player corrected by hand is laid over what was read (#286), here as in the
        // shell, so an exit they marked offered is drawn offered.
        var raidSnapshot = _raid.Corrections.Apply(runtime.Raid);
        // [V2 rough package 22] Place names, spawn areas and locked doors too, not only extracts
        // and objectives. The assembler has always adapted all five; the cockpit asked for two of
        // them, which is why V2 had no street names and why its own "Spawn areas" card was always
        // empty. Each one is still a scene layer with its own switch in the bottom strip.
        // [Issue 573] A co-op extract: hidden entirely, or drawn but never the one the game's own
        // "offered" flag highlights, unless the player asked to see co-op extracts normally.
        var coOpVisibility = _coOpExtractVisibility;
        var spawnSelection = _earlyRaidSpawns.Select(
            _map.NearbySpawnAreas,
            RaidSide(raidSnapshot.Side),
            raidSnapshot.StartedUtc);
        _spawnWindowExpiresUtc = spawnSelection.Phase == EarlyRaidSpawnPhase.Active
            ? raidSnapshot.StartedUtc + EarlyRaidSpawnPolicy.VisibleFor
            : null;
        IEnumerable<MapOverlayElement> legacyCandidates = model.OverlayElements;
        if (spawnSelection.Phase == EarlyRaidSpawnPhase.Active)
        {
            legacyCandidates = legacyCandidates.Where(element =>
                element.Layer != MapOverlayKind.Spawns || IsNearbySpawn(element, model, spawnSelection.Areas));
        }
        else if (spawnSelection.Phase == EarlyRaidSpawnPhase.Expired)
        {
            legacyCandidates = legacyCandidates.Where(element => element.Layer != MapOverlayKind.Spawns);
        }

        var legacyElements = legacyCandidates
            .Where(element => element.Layer is MapOverlayKind.Extracts or MapOverlayKind.QuestObjectives
                or MapOverlayKind.Labels or MapOverlayKind.Spawns or MapOverlayKind.Keys or MapOverlayKind.Switches)
            .Where(element => element.Layer != MapOverlayKind.Extracts ||
                coOpVisibility != CoOpExtractVisibility.Hidden || !CoOpExtracts.IsCoOp(element.Label))
            .Select(element => new MapSceneLegacyElement(
                element,
                new DataProvenance("map-catalog", nowUtc),
                element.Layer == MapOverlayKind.Extracts &&
                    CoOpExtracts.IsOffered(element.Label, coOpVisibility) &&
                    MapViewModel.IsOfferedMarker(element.Label, raidSnapshot.ActiveExtracts)
                    ? MapSceneOfferState.Offered
                    : MapSceneOfferState.Unknown))
            .ToArray();

        // The rectangle the artwork covers, in the same Leaflet units every coordinate in this
        // scene is already expressed in. It changes when the map changes and when the artwork
        // does (a tile grid and a drawing cover different ground), and the camera has to be
        // re-fitted to it whenever it does or the view opens somewhere that is no longer there.
        var previousBounds = _planBounds;
        _planBounds = PlanBoundsFor(model);
        var planBounds = _planBounds;
        var boundsChanged = previousBounds != planBounds;

        var floorIds = model.Floors.Select(floor => floor.Id).ToArray();
        UiActivity.Step("raid:legacy");
        var lootLayer = LootLayerBuildLog.Time(() => _lootSource.Build(new HighValueLootRuntimeLayerRequest(
            model.Location.Id,
            transformVersion,
            planBounds,
            nowUtc,
            _lootFilter.Filter,
            floorIds,
            LootSpawnFloorMap.OverviewFloorIds(model.Floors))));

        UiActivity.Step("raid:loot");
        var (marksLayer, markObjects) = BuildMarksLayer(_marks.Marks, model.Location.Id, nowUtc);
        // [#707] The group's pings and waypoints, drawn and not only listed.
        var (groupMarksLayer, groupMarkObjects) = BuildGroupMarksLayer(model, nowUtc);
        // [V2 rough package 22] You, your trail, the squad and where you have been before.
        var live = BuildLiveLayers(model, nowUtc);
        _objectStyles = live.Styles;
        UiActivity.Step("raid:live");
        _questScene = UserQuestMarkerScene.Apply(
            BuildQuestScene(_map.QuestSceneProjection, model, nowUtc, _questLetters),
            _userMarkers?.Markers ?? [],
            model.Location.Id,
            model.Floors);
        if (_handDone is not null)
        {
            // [Issue 571] Done, by hand or because progress the app trusts already agrees:
            // leaves the map and the list, or — "Show completed" — stays, dimmed with a check.
            var doneIds = _handDone.Entries
                .Where(mark => mark.ProfileId == _activeProfileId)
                .Select(mark => mark.ObjectiveId);
            _questScene = HandDoneObjectiveScene.Apply(_questScene, doneIds, _showCompletedObjectives);
        }
        // The renderer requires the scene to already declare the exact loot layer it is handed
        // beside it (see EnsureHighValueLootMatchesScene), so the loot layer and its objects are
        // merged in here rather than attached only through the constructor/Present overload.
        // [Issue 286] The modelled-traffic layer: its hotspots here, its picture handed to the
        // renderer once there is one (ApplyTrafficToRenderer).
        UiActivity.Step("raid:quests");
        var traffic = BuildTrafficLayers(model, planBounds, transformVersion, lootLayer, floorIds, nowUtc);
        UiActivity.Step("raid:traffic");
        var routes = BuildRouteLayers(model, transformVersion, raidSnapshot);
        UiActivity.Step("raid:routes");
        var additionalLayers = (marksLayer is { } definiteMarksLayer
            ? new[] { lootLayer.Layer, definiteMarksLayer }
            : [lootLayer.Layer]).Concat(live.Layers).Concat(traffic.Layers).Concat(routes.Layers)
            .Concat(groupMarksLayer is { } definiteGroupMarks ? new[] { definiteGroupMarks } : Array.Empty<MapSceneLayer>()).ToArray();
        var additionalObjects = lootLayer.Objects.Concat(markObjects).Concat(live.Objects).Concat(_questScene.Objects)
            .Concat(traffic.Objects).Concat(routes.Objects).Concat(groupMarkObjects).ToArray();

        // [V2 rough package 39] The stack: one asset per floor beside the background.
        // [Issue 551] Awaited before the view is read below, not after it: a zoom or a pan that
        // landed while floor artwork was loading was overwritten by the camera this rebuild had
        // read before it started waiting. Nothing between reading the view and presenting the
        // scene may yield.
        var floorAssets = await LoadFloorStackAssetsAsync(model, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        // The floor the plan is drawn on is V1's, because V1 is what fetches the artwork for it
        // and what an automatic floor change (AutoSelectsFloor) moves. The renderer's own
        // selection is pushed back into V1 by ViewChangeRequested, so the two only ever differ
        // for the moment between the two halves of one change.
        var modelFloorChanged = !string.Equals(_modelFloorId, model.SelectedFloor?.Id, StringComparison.OrdinalIgnoreCase);
        _modelFloorId = model.SelectedFloor?.Id;
        var current = Renderer;
        var preservesView = current is not null &&
            string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal) && !boundsChanged;
        var requestedView = preservesView
            ? modelFloorChanged
                ? current!.Scene.View with { SelectedFloorId = model.SelectedFloor?.Id }
                : current!.Scene.View
            // A map opens fitted: the whole plan, centred, at whatever size the card is. Zoom 1
            // is exactly that, because the projection fits the plan rectangle into the viewport
            // before the camera's own zoom is applied. It also opens the way V1 has it turned —
            // which way round a map wants to be is a fact about the map, remembered per map,
            // rather than about this session.
            : new MapSceneViewState(
                MapSceneMode.Flat2D,
                model.SelectedFloor?.Id,
                FitCamera(planBounds, Bearing()),
                []);

        // [Issue 664] This is the Layers menu's Spawns switch, defaulted on only for a new PMC
        // scene during the opening window. Once the scene exists its current switch state wins,
        // so a player can turn the nearby areas off without the next clock tick turning them on.
        if (spawnSelection.Phase == EarlyRaidSpawnPhase.Active && !preservesView)
        {
            requestedView = requestedView with
            {
                Layers = [.. requestedView.Layers.Where(layer => layer.LayerId != SpawnsLayerId), new(SpawnsLayerId, true)],
            };
        }

        // [V2 rough package 39] The mode follows V1's own "Stack" toggle, which is also what the
        // renderer's presentation control now pushes back here.
        // Not when this map cannot be stacked at all: a scene asking for a mode that will draw
        // one plan anyway lights the "Floor stack" control over a flat map, which is the exact
        // complaint V1's own stack collected ("the 3d view doesnt seem to work at all for me").
        // The refusal beside the toggle says which press would fix it.
        var mode = _map.IsStacked && model.Floors.Count > 1 && _stackRefusal.Length == 0
            ? MapSceneMode.FloorStack2D
            : MapSceneMode.Flat2D;
        if (requestedView.Mode != mode)
        {
            requestedView = requestedView with { Mode = mode };
        }

        var request = new MapSceneBuildRequest(
            Interlocked.Increment(ref _revision),
            model,
            planBounds,
            transformVersion,
            requestedView,
            legacyElements,
            additionalLayers,
            additionalObjects,
            floorAssets.Count == 0 ? [asset] : [asset, .. floorAssets]);
        UiActivity.Step("raid:floors");
        var result = _assembler.Build(request);
        UiActivity.Step("raid:assembled");
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Scene is not { } scene)
        {
            SetUnavailable(result.UnavailableReason ?? "The map scene is unavailable.");
            return;
        }

        if (Renderer is null)
        {
            var renderer = new MapSceneRendererViewModel(
                scene,
                _presentation,
                nextChangeId: Guid.NewGuid,
                reviewedAssetResolver: ResolveBackgroundImage,
                highValueLoot: lootLayer,
                highValueLootFilterState: _lootFilter,
                highValueLootCategories: null,
                // The Raid workspace's own right panel and bottom strip already show marks,
                // extracts, the raid clock and the layer switches; the renderer's own
                // search/layers/selection/loot-filter column would just be a second, narrower
                // copy of the same map beside it. Loot filtering stays reachable through
                // Renderer.HighValueLoot in this page's own right panel.
                showsDetailsPanel: false,
                // Fit is "contain, centred": the whole plan stays visible (an extract at the edge
                // is never cropped away) and the map card's own surface fills around it.
                fillsViewport: false,
                floorNameResolver: FloorName,
                styleResolver: StyleFor,
                floorElevationResolver: FloorElevation,
                ranksLootByValue: true);
            renderer.ViewChangeRequested += ViewChangeRequested;
            renderer.CameraMovedByPlayer += CameraMovedByPlayer;
            renderer.CameraZoomedByPlayer += CameraZoomedByPlayer;
            renderer.HighValueLootFilterRequested += HighValueLootFilterRequested;
            renderer.HighValueLootRefreshRequested += HighValueLootRefreshRequested;
            // [Issue 318] "Make waypoint" on a selected spawn.
            if (renderer.HighValueLoot is { } lootLayerViewModel)
            {
                lootLayerViewModel.WaypointRequested += LootWaypointRequested;
            }

            renderer.PropertyChanged += RendererPropertyChanged;
            Renderer = renderer;
            WatchLootAvailability(renderer);
            OnPropertyChanged(nameof(Renderer));
        }
        else
        {
            Renderer.Present(scene, lootLayer, _lootFilter, availableCategories: null);
        }

        UiActivity.Step("raid:presented");
        OnPropertyChanged(nameof(HasRenderer));
        ApplyTrafficToRenderer();
        // [V2 rough package 46] A renderer that was just built, or just re-presented, has to be
        // told why this floor is the one it is showing.
        PublishFloorSource();
        if (_followPending)
        {
            // A screenshot that arrived before the artwork finished decoding still moves the map
            // to the player, once there is a map to move.
            //
            // Only that case. This also used to re-follow on every rebuild whenever "Follow" was
            // on and the camera happened to be at the fit zoom, and a rebuild happens whenever
            // anything in the raid snapshot ticks — so pressing Fit, or opening a map, put the
            // view straight back to a zoomed-in crop a moment later. Following a new position is
            // what PlayerFollowRequested is for, and it still does it.
            FollowPlayer();
        }

        RefreshSceneLists(scene);
        RefreshObjectives();
        SceneRebuilt?.Invoke(this, EventArgs.Empty);
        UiActivity.Step("raid:done");
    }

    /// <summary>
    /// V2 rough package 17: raised after a scene rebuild presented, so the artwork and render
    /// model <see cref="CreateObjectivePreview"/> reads are current.
    /// </summary>
    public event EventHandler? SceneRebuilt;

    /// <summary>
    /// V2 rough package 17: a second, independent scene of the map this cockpit currently shows,
    /// carrying only the given quest objective scene objects, for the Plan workspace's centre map.
    /// </summary>
    /// <remarks>
    /// Its own renderer view model, not <see cref="Renderer"/>: the renderer keeps viewport and
    /// camera state, and two views sizing one renderer would fight over it. It reuses this
    /// cockpit's artwork, decoded bitmap and assembler, so Plan does not grow a second map
    /// pipeline. Null while this cockpit has no scene of its own.
    /// </remarks>
    internal MapSceneRendererViewModel? CreateObjectivePreview(
        IReadOnlyList<MapSceneObject> objectives,
        MapSceneRendererViewModel? existing)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        return PreviewModel() is not { } model
            ? null
            : BuildPreview(model, [], [], objectives, existing);
    }

    /// <summary>The render model a preview may be built from: only while this cockpit's own scene shows it.</summary>
    private MapRenderModel? PreviewModel() =>
        Renderer is { } current && _map.RenderModel is { } model &&
        string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal)
            ? model
            : null;

    /// <summary>
    /// The one scene-building path behind both previews (Plan's objectives, Team's marks): its
    /// own renderer view model over this cockpit's artwork, assembler and plan bounds, keeping
    /// the existing preview's camera while it still shows the same map.
    /// </summary>
    private MapSceneRendererViewModel? BuildPreview(
        MapRenderModel model,
        IReadOnlyList<MapSceneLegacyElement> legacyElements,
        IReadOnlyList<MapSceneLayer> layers,
        IReadOnlyList<MapSceneObject> objects,
        MapSceneRendererViewModel? existing)
    {
        var sameMap = existing is not null &&
            string.Equals(existing.Scene.LocationId, model.Location.Id, StringComparison.Ordinal);
        var bounds = PlanBoundsFor(model);
        var request = new MapSceneBuildRequest(
            (existing?.Scene.Revision ?? 0) + 1,
            model,
            bounds,
            model.Variant.Key,
            sameMap && existing!.Scene.Bounds == bounds
                ? existing!.Scene.View
                : new MapSceneViewState(MapSceneMode.Flat2D, model.SelectedFloor?.Id, FitCamera(bounds, 0), []),
            legacyElements,
            layers,
            objects,
            Renderer!.Scene.Assets);
        if (_assembler.Build(request).Scene is not { } scene)
        {
            return null;
        }

        if (sameMap)
        {
            existing!.Present(scene);
            return existing;
        }


        return new MapSceneRendererViewModel(
            scene,
            _presentation,
            nextChangeId: Guid.NewGuid,
            reviewedAssetResolver: ResolveBackgroundImage,
            showsDetailsPanel: false,
            fillsViewport: false);
    }

    /// <summary>Internal for direct coverage (see the unit tests).</summary>
    /// <param name="select">
    /// [Issue 594] Selects this row's own marker on the map and centres it. Left null in the
    /// direct-coverage unit tests, which build rows from raw scene objects and never click one;
    /// the row still gets a harmless no-op command rather than a null one, so nothing in the view
    /// binds against a null <see cref="RaidExtractRowViewModel.SelectCommand"/>.
    /// </param>
    internal static IReadOnlyList<RaidExtractRowViewModel> BuildExtractRows(
        IReadOnlyList<MapSceneObject> objects,
        Action<MapSceneObjectId, MapScenePoint, string>? select = null,
        string? timeLeft = null) => objects
        .Where(item => item.Kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit)
        .GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(item => item.OfferState == MapSceneOfferState.Offered).First())
        .OrderBy(item => item.OfferState switch
        {
            MapSceneOfferState.Offered => 0,
            MapSceneOfferState.Unknown => 1,
            _ => 2,
        })
        .ThenBy(item => item.Kind)
        .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
        .Select(item =>
        {
            var point = item.Geometry.Points is [var at, ..] ? at : default;
            return new RaidExtractRowViewModel(
                item.Id,
                point,
                item.Label,
                item.Kind == MapSceneObjectKind.Transit ? "Transit" : item.Faction switch
                {
                    MapFeatureFaction.Pmc => "PMC",
                    MapFeatureFaction.Scav => "Scav",
                    MapFeatureFaction.Shared => "PMC · Scav",
                    _ => "",
                },
                item.OfferState,
                select is null ? NoOpCommand : new DelegateCommand(() => select(item.Id, point, item.Label)),
                item.ExtractRequirements,
                timeLeft);
        })
        .ToArray();

    private static readonly ICommand NoOpCommand = new DelegateCommand(() => { });

    /// <summary>
    /// The objectives the selected map's quests ask for, as scene objects and as the entries that
    /// explain them. Empty until the quest layer has been read for this very map and artwork: a
    /// projection made for another variant carries another variant's transform.
    /// </summary>
    internal static QuestObjectiveScene BuildQuestScene(
        QuestMapProjectionReadModel? projection,
        MapRenderModel model,
        DateTimeOffset nowUtc,
        QuestObjectiveLetterAssignment? letters = null)
    {
        if (projection is null ||
            !string.Equals(projection.LocationId, model.Location.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(projection.VariantKey, model.Variant.Key, StringComparison.OrdinalIgnoreCase))
        {
            return QuestObjectiveScene.Empty;
        }

        var builder = new QuestObjectiveSceneBuilder();
        if (letters is null)
        {
            return builder.Build(projection.Objectives, model.Floors, null, nowUtc);
        }

        // [Issue 571] A first pass just to learn which objectives are placed this rebuild — the
        // second pass hands every one of those its stable letter through numberFor, so an
        // objective that leaves the map (done, or its quest no longer active) never shifts the
        // letter of one still on it. See QuestObjectiveLetterAssignment.
        var natural = builder.Build(projection.Objectives, model.Floors, null, nowUtc);
        var placedIds = natural.Entries
            .Where(entry => entry.IsPlaced)
            .Select(entry => entry.ObjectiveId)
            .ToHashSet(StringComparer.Ordinal);
        return builder.Build(
            projection.Objectives,
            model.Floors,
            id => placedIds.Contains(id) ? letters.LetterFor(id) : null,
            nowUtc);
    }

    private void RefreshObjectives()
    {
        var entries = _questScene.Entries;
        if (_selectedObjectiveId is not null && entries.All(entry => entry.ObjectiveId != _selectedObjectiveId))
        {
            _selectedObjectiveId = null;
        }

        // [Issue 571] Done, the same picture the map uses: a hand mark for this profile, or
        // progress the app already trusts saying so.
        var doneIds = (_handDone?.Entries ?? [])
            .Where(mark => mark.ProfileId == _activeProfileId)
            .Select(mark => mark.ObjectiveId)
            .ToHashSet(StringComparer.Ordinal);
        bool IsDone(QuestObjectiveEntry entry) =>
            doneIds.Contains(entry.ObjectiveId) || entry.Objective.ObjectiveState == RecordedObjectiveState.Completed;

        var signature = string.Join('|', entries.Select(entry => string.Join(
            ':',
            entry.ObjectiveId,
            entry.Number,
            entry.PlacementLabel,
            entry.FloorLabel,
            entry.Objective.TaskState,
            entry.Objective.ObjectiveState,
            entry.Objective.RecordedCount,
            entry.Objective.IsTaskPinned,
            entry.Objective.IsObjectivePinned,
            IsDone(entry)))) + "#" + _selectedObjectiveId + "#" + _showCompletedObjectives;
        if (signature == _objectiveSignature)
        {
            return;
        }

        _objectiveSignature = signature;
        var toggleDone = _handDone is null ? null : (Action<string>)ToggleObjectiveDone;
        QuestObjectives = entries
            .OrderBy(entry => entry.IsPlaced ? 0 : 1)
            .Select(entry => new RaidObjectiveRowViewModel(entry, SelectObjective, IsDone(entry), toggleDone)
            {
                IsSelected = entry.ObjectiveId == _selectedObjectiveId,
            })
            .ToArray();
        SelectedObjective = entries.FirstOrDefault(entry => entry.ObjectiveId == _selectedObjectiveId) is { } selected
            ? new RaidObjectiveDetailViewModel(
                selected,
                _map.NameOfItem,
                uri => _wikiOpener?.TryOpen(uri) == true,
                ClearObjectiveSelection,
                _userMarkers is null ? null : ArmObjective,
                _userMarkers is null ? null : RemoveObjectiveMarker,
                IsDone(selected),
                toggleDone)
            : null;
        OnPropertyChanged(nameof(QuestObjectives));
        OnPropertyChanged(nameof(HasQuestObjectives));
        OnPropertyChanged(nameof(QuestObjectiveSummary));
        OnPropertyChanged(nameof(SelectedObjective));
        OnPropertyChanged(nameof(HasSelectedObjective));
    }

    /// <summary>
    /// Selects an objective from its row: shows what it is, marks it on the plan where it has a
    /// place that is on the floor being shown, and brings the plan to it.
    /// </summary>
    private void SelectObjective(string objectiveId)
    {
        // Choosing another objective abandons a placement waiting for this one.
        if (_armedObjectiveId is not null && _armedObjectiveId != objectiveId)
        {
            ArmObjective(null);
        }

        _selectedObjectiveId = objectiveId;
        if (Renderer is { } renderer &&
            _questScene.Entries.FirstOrDefault(entry => entry.ObjectiveId == objectiveId) is { } entry)
        {
            var visible = renderer.Scene.VisibleObjects;
            var marker = entry.ObjectIds
                .Select(id => visible.FirstOrDefault(item => item.Id == id && item.Geometry.Kind == MapSceneGeometryKind.Point))
                .FirstOrDefault(item => item is not null);
            if (marker is not null)
            {
                renderer.SelectObject(marker.Id);
                renderer.FocusOn(marker.Geometry.Points[0], 2);
            }
        }

        RefreshObjectives();
    }

    private void ClearObjectiveSelection()
    {
        ArmObjective(null);
        _selectedObjectiveId = null;
        Renderer?.ClearSelection();
        RefreshObjectives();
    }

    /// <summary>Following the plan: selecting an objective's marker on it selects the objective here.</summary>
    private void RendererPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapSceneRendererViewModel.ShowsTrafficHeat))
        {
            OnPropertyChanged(nameof(ShowsTrafficBanner));
        }

        // [Issue 594] The map's own selection changed — from a marker click or from a row's
        // SelectCommand, either way — so the Extract options row and the "Selected" card follow it.
        if (e.PropertyName == nameof(MapSceneRendererViewModel.SelectedObject))
        {
            SyncExtractSelection();
            RevealSelectedExtract();
            OnPropertyChanged(nameof(ShowsSelectedExtract));
            OnPropertyChanged(nameof(SelectedExtractEstimate));
            OnPropertyChanged(nameof(HasSelectedExtractEstimate));
        }

        // [Issue 286] With no screenshot yet, a selected spawn is where routes start from.
        // Only when the spawn itself changed: every re-present raises SelectedObject again, and a
        // rebuild per raise is a rebuild loop.
        if (e.PropertyName == nameof(MapSceneRendererViewModel.SelectedObject) && _map.PlayerPosition is null &&
            SelectedSpawnId() != _routeStartSpawnId)
        {
            _rebuildRequest.Request();
        }

        if (e.PropertyName != nameof(MapSceneRendererViewModel.SelectedObject) ||
            Renderer?.SelectedObject?.SceneObject is not { } selected)
        {
            return;
        }

        var objectiveId = _questScene.EntryFor(selected.Id)?.ObjectiveId;
        if (objectiveId == _selectedObjectiveId)
        {
            return;
        }

        _selectedObjectiveId = objectiveId;
        RefreshObjectives();
    }

    private void RefreshSceneLists(MapSceneSnapshot scene)
    {
        // [Issue 573] A co-op extract is not offered as a choice here unless the player asked for
        // Normal — "co-op extracts shouldnt highlight as options on the map".
        var extractObjects = _coOpExtractVisibility == CoOpExtractVisibility.Normal
            ? scene.Objects
            : scene.Objects.Where(item => item.Kind != MapSceneObjectKind.Extract || !CoOpExtracts.IsCoOp(item.Label)).ToArray();
        // The corrections card lists every exit by name; the rows then take their routes' estimates.
        var extractRows = BuildExtractRows(extractObjects, SelectExtract, _raid.TimeLeft);
        Corrections.Refresh(
            _raid.Corrections.Apply(_stateStore.Current.Raid),
            [.. extractRows.Where(row => row.Detail != "Transit").Select(row => row.Name)]);
        // [#453] Kept when every row reads the same: a new list makes the panel build every row's
        // controls again, and a squadmate moving rebuilt the scene three times a second.
        var rows = WithRouteEstimates(extractRows);
        var extractsChanged = !RaidExtractRowViewModel.ReadSame(MapExtracts, rows);
        if (extractsChanged)
        {
            MapExtracts = rows;
        }

        // [Issue 594] Every rebuild replaces the rows with fresh instances (WithRouteEstimates
        // included), which would otherwise silently drop the highlight on whichever one the
        // player had selected before the last screenshot came in.
        SyncExtractSelection();
        var spawnAreas = scene.Objects
            .Where(item => item.Kind == MapSceneObjectKind.SpawnArea)
            .Select(item => item.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var spawnAreasChanged = !SpawnAreas.SequenceEqual(spawnAreas, StringComparer.Ordinal);
        if (spawnAreasChanged)
        {
            SpawnAreas = spawnAreas;
        }

        if (extractsChanged)
        {
            OnPropertyChanged(nameof(MapExtracts));
            OnPropertyChanged(nameof(HasMapExtracts));
        }

        if (spawnAreasChanged)
        {
            OnPropertyChanged(nameof(SpawnAreas));
            OnPropertyChanged(nameof(HasSpawnAreas));
        }

        OnPropertyChanged(nameof(MapTitle));
        OnPropertyChanged(nameof(MapSummary));
        // [V2 rough package 39] The stack's own readout belongs to the renderer, which has just
        // rebuilt it; nothing on MapViewModel changes when a plate arrives or fails to.
        OnPropertyChanged(nameof(HasFloorStack));
        OnPropertyChanged(nameof(StackStatus));
        OnPropertyChanged(nameof(HasStackStatus));
        // The attribution belongs to the asset the renderer has just resolved.
        OnPropertyChanged(nameof(ArtworkAttribution));
        OnPropertyChanged(nameof(HasArtworkAttribution));
    }

    private void ViewChangeRequested(MapSceneViewChange change)
    {
        if (Renderer is null)
        {
            return;
        }

        var result = MapSceneViewReducer.Apply(Renderer.Scene, change);
        if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
        {
            Renderer.Present(result.Scene);
        }

        // [V2 rough package 22] A floor change has to reach V1 too: V1 owns the floor the
        // artwork is rasterized for, so a renderer-only change would filter the markers to the
        // new floor while leaving the old floor's picture underneath them.
        // [V2 rough package 39] So does a presentation change. The renderer offers "Floor stack"
        // whenever the map has floors; V1 owns whether the stack is on, and owns loading the
        // per-floor artwork the next rebuild hands back.
        if (change.Kind == MapSceneViewChangeKind.SetMode &&
            result.Status == MapSceneViewChangeStatus.Applied &&
            change.Mode is { } requestedMode &&
            _map.CanStack)
        {
            var wantsStack = requestedMode == MapSceneMode.FloorStack2D;
            if (_map.IsStacked != wantsStack)
            {
                _map.IsStacked = wantsStack;
            }
        }

        if (change.Kind == MapSceneViewChangeKind.SelectFloor &&
            result.Status == MapSceneViewChangeStatus.Applied &&
            _map.Floors.FirstOrDefault(floor => string.Equals(floor.Id, change.FloorId, StringComparison.OrdinalIgnoreCase)) is { } selected &&
            !string.Equals(_map.SelectedFloor?.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _ = _map.SelectFloorAsync(selected);
        }
    }

    /// <summary>The catalog's own name for a floor the scene only knows as an id.</summary>
    private string FloorName(string floorId) => _map.Floors
        .FirstOrDefault(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase))?.Name ?? floorId;

    /// <summary>
    /// [V2 rough package 39] How high a floor sits, from the catalog's own height bands, so the
    /// stack and the floor ladder are both in the order somebody walks the building rather than
    /// the order upstream happened to list them. <see cref="FloorStack.Elevation"/> is V1's rule
    /// unchanged; only the lookup from a scene floor id is new.
    /// </summary>
    private double? FloorElevation(string floorId) => _map.Floors
        .FirstOrDefault(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)) is { } definition
            ? FloorStack.Elevation(definition)
            : null;

    /// <summary>How this cockpit wants one object drawn; see <see cref="MapSceneObjectStyle"/>.</summary>
    private MapSceneObjectStyle? StyleFor(MapSceneObject item) =>
        _objectStyles.TryGetValue(item.Id, out var style) ? style
        : _routeStyles.TryGetValue(item.Id, out var route) ? route
        // [Issue 573] A co-op extract at "Dim" is drawn faded, so it never competes for attention
        // with one the player can use alone.
        : item.Kind == MapSceneObjectKind.Extract && _coOpExtractVisibility == CoOpExtractVisibility.Dim &&
            CoOpExtracts.IsCoOp(item.Label)
            ? new MapSceneObjectStyle(Opacity: 0.45)
            : null;

    /// <summary>V1's remembered quarter turn for this map, as a scene camera bearing.</summary>
    private double Bearing() => (_map.RotationDegrees % 360 + 360) % 360;

    /// <summary>The rectangle the reviewed map covers, as scene bounds.</summary>
    /// <remarks>
    /// Internal for direct coverage: this one line decides whether every marker on the map lands
    /// on the artwork or beside it (see the marker-landing tests).
    ///
    /// [V2 rough package 46] The reviewed map's rectangle, not the tile grid's. The grid snaps
    /// outwards to whole tiles and is always squarer than the map it carries, so fitting it drew
    /// Customs at 1.70 wide-to-tall when Customs is 1.97, with a blank band of tiles above and
    /// below. <see cref="ComposeTileArtwork"/> crops the mosaic to exactly this rectangle, so the
    /// artwork and the objects still share one rectangle — #413's contract — and that rectangle
    /// is now the map's own shape.
    /// </remarks>
    internal static MapSceneBounds PlanBoundsFor(MapRenderModel model) =>
        (MapPlanProjection.Reviewed(model) ?? MapPlanProjection.For(model)) is { IsValid: true } rect
            ? new(rect.MinimumX, rect.MinimumY, rect.MaximumX, rect.MaximumY)
            : UnplaceablePlanBounds;

    /// <summary>
    /// The whole plan, centred: the camera a map opens on and the one "Fit" returns to.
    /// </summary>
    /// <remarks>
    /// Zoom 1 is a fit rather than an arbitrary scale because the renderer's projection already
    /// fits the plan rectangle into the viewport, at whatever size the card happens to be,
    /// before the camera's zoom multiplies it.
    /// </remarks>
    private static MapSceneCamera FitCamera(MapSceneBounds bounds, double bearingDegrees) => new(
        bounds.MinimumX + (bounds.Width / 2),
        bounds.MinimumY + (bounds.Height / 2),
        1,
        bearingDegrees,
        0);

    private void HighValueLootFilterRequested(HighValueLootFilterRequest request)
    {
        if (Renderer is null || _map.RenderModel is not { } model ||
            request.ExpectedRevision != Renderer.Scene.Revision ||
            !string.Equals(request.LocationId, Renderer.Scene.LocationId, StringComparison.Ordinal) ||
            !string.Equals(request.TransformVersion, Renderer.Scene.TransformVersion, StringComparison.Ordinal))
        {
            return;
        }

        _lootValueFilter.Set(request.State.Filter);
        _lootFilter = request.State;
        var result = _lootSource.Build(new HighValueLootRuntimeLayerRequest(
            model.Location.Id,
            request.TransformVersion,
            _planBounds,
            _timeProvider.GetUtcNow(),
            request.State.Filter,
            model.Floors.Select(floor => floor.Id).ToArray(),
            LootSpawnFloorMap.OverviewFloorIds(model.Floors)));
        var scene = ReplaceLootObjects(Renderer.Scene, result);
        Renderer.Present(scene, result, request.State, availableCategories: null);
    }

    /// <summary>[Issue 563] "High-value loot only" with no data offers Refresh instead of
    /// blanking the map; this is what that action, and the loot panel's own Refresh button,
    /// runs.</summary>
    private void HighValueLootRefreshRequested() => _ = RefreshLootDataAsync();

    private async Task RefreshLootDataAsync()
    {
        // Reruns the same production import a catalog sync triggers (#318/#563), so a manual
        // retry does not need a full catalog refresh to try again. The scene rebuild afterwards
        // picks up either a newly published snapshot or a fresh failure reason.
        await _lootSource.RefreshAsync(force: true, CancellationToken.None).ConfigureAwait(true);
        await RebuildAsync().ConfigureAwait(true);
    }

    private static MapSceneSnapshot ReplaceLootObjects(MapSceneSnapshot scene, HighValueLootLayerResult loot) => new(
        scene.Revision + 1,
        scene.LocationId,
        scene.VariantKey,
        scene.TransformVersion,
        scene.Bounds,
        scene.FloorIds,
        scene.Capabilities,
        scene.View,
        scene.Layers.Where(layer => layer.Id != HighValueLootLayerService.LayerId).Append(loot.Layer).ToArray(),
        scene.Objects.Where(item => item.LayerId != HighValueLootLayerService.LayerId).Concat(loot.Objects).ToArray(),
        scene.Assets);

    /// <summary>
    /// [V2 rough package 22] What a live-evidence build produced: the layers to declare, the
    /// objects to draw, and how this cockpit wants each of them drawn.
    /// </summary>
    internal sealed record LiveSceneLayers(
        IReadOnlyList<MapSceneLayer> Layers,
        IReadOnlyList<MapSceneObject> Objects,
        IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> Styles);

    /// <summary>
    /// [V2 rough package 22] Everything the live layers are built from, read off the V1 map view
    /// model.
    /// </summary>
    /// <remarks>
    /// Passed in rather than read from <see cref="_map"/> inside the builder so the mapping from
    /// observed evidence to scene objects can be covered directly, without standing up a whole
    /// map page, a catalog and a runtime store to place one screenshot.
    /// </remarks>
    internal sealed record LiveSceneInputs(
        ScreenshotPosition? Player,
        IReadOnlyList<ScreenshotPosition> PlayerTrail,
        IReadOnlyList<GroupMemberView> Squad,
        Func<string?, bool> IsOnThisMap,
        Func<string, string> ColorFor,
        bool ShowsGroupNames,
        IReadOnlyList<RaidTrail> Visited,
        bool ShowsVisited)
    {
        public IReadOnlyList<WorldPosition> PlannedRoute { get; init; } = [];
    }

    /// <summary>Colours, chosen here because they are presentation and the scene carries none.</summary>
    private const string PlayerColor = "#FF34D3E8";
    private const string VisitedColor = "#8056B8C6";

    /// <summary>A screenshot older than this is drawn faded, because the player has moved since.</summary>
    private static readonly TimeSpan PositionFreshFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// You, where you have walked this raid, your squad, and where past raids on this map put
    /// you — as scene layers the canonical renderer already knows how to draw.
    /// </summary>
    /// <remarks>
    /// All four were already in <see cref="MapViewModel"/> and drawn by the V1 map page; none of
    /// it is recomputed here. What is recomputed is the projection: V1's view models carry
    /// positions in its own zoomed canvas pixels, and this renderer works in the plan's percent
    /// box, so the world positions behind them are projected again through the same
    /// <c>TryMapPosition</c> V1 uses rather than being un-projected back out of pixels.
    ///
    /// Headings are turned by the artwork's own rotation for the same reason V1 turns them: the
    /// screenshot records a bearing in the world, and the map is drawn with the world turned.
    /// </remarks>
    private LiveSceneLayers BuildLiveLayers(MapRenderModel model, DateTimeOffset nowUtc)
    {
        var player = _map.PlayerPosition;
        var built = BuildLiveLayers(
            new LiveSceneInputs(
                player,
                _map.PlayerTrailPositions,
                _map.GroupMembers,
                _map.IsOnOpenMap,
                _map.GroupColorFor,
                _map.ShowsGroupNames,
                _map.VisitedRaids,
                _map.ShowsVisited)
            {
                PlannedRoute = _map.PlannedReplayPositions,
            },
            model,
            nowUtc);
        // Only a marker that is still fresh has a moment to wait for. Recomputed by every rebuild,
        // so a tick that lands a hair early just waits for the next.
        _positionStaleAtUtc = player is { } position && nowUtc - position.Timestamp.ToUniversalTime() <= PositionFreshFor
            ? position.Timestamp.ToUniversalTime() + PositionFreshFor
            : null;
        return built;
    }

    internal static LiveSceneLayers BuildLiveLayers(LiveSceneInputs inputs, MapRenderModel model, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(model);
        var layers = new List<MapSceneLayer>(3);
        var objects = new List<MapSceneObject>();
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        var artworkRotation = model.Variant.Transform?.RotationDegrees ?? 0;

        // --- you ------------------------------------------------------------------------
        var playerObjects = new List<MapSceneObject>(2);
        if (inputs.Player is { } position && TryPlan(model, position.Position, out var here))
        {
            var heading = Bearing(position.HeadingDegrees, artworkRotation);
            var stale = nowUtc - position.Timestamp.ToUniversalTime() > PositionFreshFor;
            playerObjects.Add(new(
                new(PlayerObjectId),
                PlayerLayerId,
                MapSceneObjectKind.LastKnownPosition,
                MapSceneTruthKind.LocalLastKnown,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"You · {LocalTime.Time(position.Timestamp)} · facing {heading:F0}°"),
                stale ? "From an older screenshot — you have probably moved since." : null,
                MapSceneGeometry.At(here),
                [],
                new DataProvenance("screenshot", position.Timestamp.ToUniversalTime()),
                headingDegrees: heading));
            styles[new(PlayerObjectId)] = new(PlayerColor, Opacity: stale ? 0.65 : 1);
        }

        var trail = PlanPoints(model, inputs.PlayerTrail.Select(step => step.Position));
        if (trail.Count > 1)
        {
            playerObjects.Add(new(
                new(PlayerTrailObjectId),
                PlayerLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.LocalLastKnown,
                "Your path this raid",
                null,
                new(MapSceneGeometryKind.Line, trail),
                [],
                new DataProvenance("screenshot", nowUtc)));
            styles[new(PlayerTrailObjectId)] = new(PlayerColor, LineThickness: 3);
        }

        var plannedRoute = PlanPoints(model, inputs.PlannedRoute);
        if (plannedRoute.Count > 1)
        {
            playerObjects.Add(new(
                new(PlannedReplayObjectId),
                PlayerLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.PersonalPlan,
                "Your planned route",
                "Selected on the Raid page before these screenshot observations.",
                new(MapSceneGeometryKind.Line, plannedRoute),
                [],
                new DataProvenance("personal-plan", nowUtc)));
            styles[new(PlannedReplayObjectId)] = new("#FFF1C75B", LineThickness: 4, Opacity: 0.95);
        }

        if (playerObjects.Count > 0)
        {
            layers.Add(new(PlayerLayerId, "You", 70, true));
            objects.AddRange(playerObjects);
        }

        // --- squad ----------------------------------------------------------------------
        var squadObjects = new List<MapSceneObject>();
        foreach (var member in inputs.Squad)
        {
            // [#707] A squadmate who has left their raid is not on this map any more, whatever
            // their older companion still says about the last screenshot of it.
            if (!inputs.IsOnThisMap(member.MapId) || !SquadRaidPresence.IsPlaced(member))
            {
                continue;
            }

            // [Issue 581] inputs.ColorFor (MapViewModel.GroupColorFor) hands back an already
            // "#AARRGGBB"-formatted colour, the same source the squad panel's own chip reads.
            var color = inputs.ColorFor(member.Name);
            if (member.Position is { } memberPosition && TryPlan(model, memberPosition, out var at))
            {
                var id = new MapSceneObjectId($"squad:{member.Name}");
                var age = member.PositionAgeNow ?? TimeSpan.MaxValue;
                squadObjects.Add(new(
                    id,
                    SquadLayerId,
                    MapSceneObjectKind.TeammateLastKnown,
                    MapSceneTruthKind.TeamSharedLastKnown,
                    member.Name,
                    Describe(age),
                    MapSceneGeometry.At(at),
                    [],
                    new DataProvenance("group-relay", nowUtc),
                    headingDegrees: member.HeadingDegrees is { } memberHeading
                        ? Bearing(memberHeading, artworkRotation)
                        : null));
                styles[id] = new(color, Opacity: age > PositionFreshFor || member.HasGoneQuiet ? 0.6 : 1);

                // V1's "Names": the squadmate's name written beside their marker. Here that is a
                // label object, so it goes through the same place-name text the plan already
                // draws rather than growing a second way to write on the map.
                if (inputs.ShowsGroupNames)
                {
                    var nameId = new MapSceneObjectId($"squad-name:{member.Name}");
                    squadObjects.Add(new(
                        nameId,
                        SquadLayerId,
                        MapSceneObjectKind.Label,
                        MapSceneTruthKind.TeamSharedLastKnown,
                        member.Name,
                        null,
                        MapSceneGeometry.At(new(at.X, at.Y + 1.4)),
                        [],
                        new DataProvenance("group-relay", nowUtc)));
                    styles[nameId] = new(color);
                }
            }

            var memberTrail = PlanPoints(
                model,
                member.Trail.Select(step => new WorldPosition(step.X, 0, step.Z)));
            if (memberTrail.Count > 1)
            {
                var trailId = new MapSceneObjectId($"squad-trail:{member.Name}");
                squadObjects.Add(new(
                    trailId,
                    SquadLayerId,
                    MapSceneObjectKind.Route,
                    MapSceneTruthKind.TeamSharedLastKnown,
                    $"{member.Name}'s path",
                    null,
                    new(MapSceneGeometryKind.Line, memberTrail),
                    [],
                    new DataProvenance("group-relay", nowUtc)));
                styles[trailId] = new(color, LineThickness: 2, Opacity: 0.8);
            }
        }

        if (squadObjects.Count > 0)
        {
            layers.Add(new(SquadLayerId, "Squad", 60, true));
            objects.AddRange(squadObjects);
        }

        // --- visited --------------------------------------------------------------------
        // Off unless asked for, exactly as in V1: it is the one layer that grows with how much
        // somebody has played, and on a map they know well it is a great deal of ink over the
        // things they opened the map to read. MapViewModel loads it on demand, so when the
        // toggle is off there is nothing here to draw anyway.
        var visitedObjects = new List<MapSceneObject>();
        var visited = inputs.Visited;
        for (var index = 0; index < visited.Count; index++)
        {
            var points = PlanPoints(model, visited[index].Positions.Select(step => step.Position));
            if (points.Count < 2)
            {
                continue;
            }

            var id = new MapSceneObjectId($"visited:{visited[index].RaidId}");
            visitedObjects.Add(new(
                id,
                VisitedLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.LocalLastKnown,
                visited[index].StartedUtc is { } started
                    ? $"Raid on {LocalTime.Date(started)}"
                    : "An earlier raid",
                null,
                new(MapSceneGeometryKind.Line, points),
                [],
                new DataProvenance("raid-history", nowUtc)));
            // Oldest faintest, as V1 draws them: the list arrives newest first.
            styles[id] = new(
                VisitedColor,
                LineThickness: 1.5,
                Opacity: visited.Count <= 1 ? 0.55 : 0.55 - (0.35 * index / (visited.Count - 1.0)));
        }

        if (visitedObjects.Count > 0)
        {
            layers.Add(new(VisitedLayerId, "Visited", 20, inputs.ShowsVisited));
            objects.AddRange(visitedObjects);
        }

        return new(layers, objects, styles);
    }

    /// <summary>A world bearing read off a screenshot, turned into a bearing on the drawn plan.</summary>
    /// <remarks>Internal for direct coverage: pointing the cone the wrong way by whatever the
    /// artwork is rotated by is the bug this exists to prevent, and on some maps that is a
    /// quarter or a half turn.</remarks>
    internal static double Bearing(double headingDegrees, double artworkRotationDegrees) =>
        double.IsFinite(headingDegrees) ? ((headingDegrees - artworkRotationDegrees) % 360 + 360) % 360 : 0;

    /// <summary>
    /// How old a squadmate's position is, in steps of fifteen seconds under a minute.
    /// </summary>
    /// <remarks>
    /// Written to the second, this text differed at every exchange for a teammate who had not
    /// moved, and any difference in a scene object makes the plan recreate all of its markers
    /// (about 40 MB and a third of a second on Customs). The exchange itself is five seconds
    /// apart, so the seconds were never more than a guess.
    /// </remarks>
    internal static string Describe(TimeSpan age) => age == TimeSpan.MaxValue
        ? "Position unknown"
        : age < TimeSpan.FromSeconds(15)
            ? "From a screenshot just now"
            : age < TimeSpan.FromMinutes(1)
                ? string.Create(CultureInfo.CurrentCulture, $"From a screenshot {(int)age.TotalSeconds / 15 * 15}s ago")
                : string.Create(CultureInfo.CurrentCulture, $"From a screenshot {(int)age.TotalMinutes}m ago");

    private static bool TryPlan(MapRenderModel model, WorldPosition position, out MapScenePoint point)
    {
        point = default;
        if (!model.TryMapPosition(position, out var mapped) ||
            !double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
        {
            return false;
        }

        point = new(mapped.X, mapped.Y);
        return true;
    }

    /// <summary>
    /// A run of world positions as a plan line, dropping the ones this transform cannot place.
    /// </summary>
    /// <remarks>
    /// Points outside the plan's own bounds are dropped rather than clamped: the renderer will
    /// not draw a line that leaves the plan at all (see its bounds check), so one stray
    /// position from a bad transform would otherwise take the whole trail with it.
    /// </remarks>
    private static IReadOnlyList<MapScenePoint> PlanPoints(MapRenderModel model, IEnumerable<WorldPosition> positions)
    {
        var bounds = PlanBoundsFor(model);
        var points = new List<MapScenePoint>();
        foreach (var position in positions)
        {
            if (TryPlan(model, position, out var point) && bounds.Contains(point))
            {
                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>Internal for direct coverage of the marks-to-scene mapping (see the unit tests).</summary>
    internal static (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects) BuildMarksLayer(
        IReadOnlyList<RaidMark> marks,
        string mapId,
        DateTimeOffset nowUtc)
    {
        var labeled = LabelMarksForMap(marks, mapId);
        if (labeled.Count == 0)
        {
            return (null, []);
        }

        var layer = new MapSceneLayer(MarksLayerId, "My marks", 40, true);
        var objects = labeled
            .Select(item => new MapSceneObject(
                new($"{MarkIdPrefix}{item.Mark.Id}"),
                MarksLayerId,
                item.Mark.Kind == RaidMarkKind.Ping ? MapSceneObjectKind.Ping : MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.UserAuthored,
                item.Label,
                null,
                MapSceneGeometry.At(new(item.Mark.State.X, item.Mark.State.Y)),
                item.Mark.State.FloorId is null ? [] : [item.Mark.State.FloorId],
                new DataProvenance("local-mark", nowUtc),
                // Issue 584: carries the store's own expiry straight through, so a ping fades and
                // drops on the map the same moment it drops from the marks list.
                expiresUtc: item.Mark.State.ExpiresUtc))
            .ToArray();
        return (layer, objects);
    }

    /// <summary>
    /// V2 rough package 17 (team): a second, independent scene of the map this cockpit shows,
    /// carrying only the group's marks, for the Team workspace's centre map.
    /// </summary>
    /// <remarks>
    /// Built by the same BuildPreview as the Plan workspace's objective preview: its own renderer view model
    /// (a renderer owns its viewport and camera, so two views cannot share one) over this
    /// cockpit's artwork, assembler and plan bounds, so Team does not grow a second map
    /// pipeline. <paramref name="buildMarks"/> is handed the map's id, which relay map ids
    /// belong to it, and the world-to-plan projection, and returns the layer and objects to
    /// draw. Null while this cockpit has no scene of its own.
    /// </remarks>
    internal MapSceneRendererViewModel? CreateMarksPreview(
        Func<string, Func<string, bool>, Func<WorldPosition, MapScenePoint?>, (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects)> buildMarks,
        MapSceneRendererViewModel? existing)
    {
        ArgumentNullException.ThrowIfNull(buildMarks);
        if (PreviewModel() is not { } model)
        {
            return null;
        }

        var compatible = TarkovCompanion.Application.Services.Quests.QuestMapProjectionService.CompatibleMapIds(model.Location, model.Variant);
        var (layer, objects) = buildMarks(
            model.Location.Id,
            mapId => compatible.Contains(mapId),
            position => model.TryMapPosition(position, out var point) && double.IsFinite(point.X) && double.IsFinite(point.Y)
                ? new MapScenePoint(point.X, point.Y)
                : null);
        return BuildPreview(model, [], layer is null ? [] : [layer], objects, existing);
    }

    /// <summary>
    /// Every mark on a map, in placement order, paired with the label its scene object and its
    /// row in the marks list must both show.
    /// </summary>
    /// <remarks>
    /// Built in one pass over one order, the same reason <c>MapViewModel.UpdateGroupMarks</c>
    /// numbers its own list inside the loop that draws it: a row numbered by a separate pass
    /// over a separately filtered or sorted collection can drift from the dot it names the
    /// moment one collection skips something the other kept. A waypoint with no custom name is
    /// numbered among the waypoints on this map only; a ping is always "Ping" and never carries
    /// a custom name — see <see cref="RaidMarkRowViewModel.CanRename"/>.
    /// </remarks>
    internal static IReadOnlyList<(RaidMark Mark, string Label)> LabelMarksForMap(
        IReadOnlyList<RaidMark> marks,
        string mapId)
    {
        var ordered = marks
            .Where(mark => string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mark => mark.CreatedUtc)
            .ThenBy(mark => mark.Id)
            .ToArray();
        var result = new List<(RaidMark, string)>(ordered.Length);
        var waypointNumber = 0;
        foreach (var mark in ordered)
        {
            if (mark.Kind == RaidMarkKind.Ping)
            {
                result.Add((mark, "Ping"));
                continue;
            }

            waypointNumber++;
            var label = string.IsNullOrWhiteSpace(mark.State.Label)
                ? waypointNumber.ToString(CultureInfo.InvariantCulture)
                : mark.State.Label!;
            result.Add((mark, label));
        }

        return result;
    }

    /// <summary>
    /// What to say while there is no map to draw: what V1 is doing about it, in V1's own words.
    /// </summary>
    /// <remarks>
    /// It was "No map is loaded yet." whatever was happening, which on a first launch was the
    /// whole page for as long as the map list and the first map took to download. V1's status
    /// already says which of those it is ("Loading the tarkov.dev map catalog…", "Loading Customs
    /// · interactive…") and, when the list cannot be fetched at all, that and why.
    /// </remarks>
    private string WaitingForMap() => string.IsNullOrWhiteSpace(_map.Status) ? "Loading maps…" : _map.Status;

    private void SetUnavailable(string reason)
    {
        if (Renderer is not null)
        {
            Renderer.ViewChangeRequested -= ViewChangeRequested;
            Renderer.HighValueLootFilterRequested -= HighValueLootFilterRequested;
            Renderer.HighValueLootRefreshRequested -= HighValueLootRefreshRequested;
            Renderer = null;
            WatchLootAvailability(null);
            OnPropertyChanged(nameof(Renderer));
            OnPropertyChanged(nameof(HasRenderer));
        }

        UnavailableReason = reason;
    }
}
