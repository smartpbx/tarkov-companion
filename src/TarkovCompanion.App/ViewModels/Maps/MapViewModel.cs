using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

public sealed record MapTileViewModel(
    string LocalPath,
    Bitmap Image,
    double Left,
    double Top,
    int Size,
    bool HasArtwork);

/// <summary>
/// The counter-scale that keeps a marker the same size on screen at every zoom.
/// </summary>
/// <remarks>
/// Everything on the map sits inside one canvas that is scaled as a whole, so a marker drawn
/// twenty pixels wide was three pixels when the map was fitted and a hundred and sixty when a
/// building was read up close, and its name went from unreadable to a billboard. Each marker
/// undoes the zoom with a scale of its own, and every one of them reads that scale from this
/// single object, so a turn of the wheel is one property change rather than a rebuild of
/// every marker on the map.
/// </remarks>
public sealed class MapMarkerScale : INotifyPropertyChanged
{
    private double _inverse = 1;

    /// <summary>A scale that never changes, for a marker built without a map to follow.</summary>
    public static MapMarkerScale Unscaled { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One over the map's zoom, applied to the marker's own transform.</summary>
    public double Inverse
    {
        get => _inverse;
        private set
        {
            if (_inverse.Equals(value))
            {
                return;
            }

            _inverse = value;
            PropertyChanged?.Invoke(this, new(nameof(Inverse)));
        }
    }

    public void Follow(double zoom) =>
        Inverse = double.IsFinite(zoom) && zoom > 0 ? 1 / zoom : 1;
}

/// <summary>
/// Where one marker's name sits, and whether it is drawn at all.
/// </summary>
/// <remarks>
/// Held apart from the marker so that arranging the names does not rebuild the marker list.
/// Zoom changes continuously while somebody scrolls, and the arrangement changes with it;
/// replacing eighty records on every wheel notch would throw away the selection and the hover
/// along with them.
///
/// Names used to disappear as a group below a zoom threshold, which is why they blinked in and
/// out instead of moving. Each one now finds its own slot, and only the ones that fit nowhere
/// are dropped.
/// </remarks>
public sealed class MapNamePlacement : INotifyPropertyChanged
{
    private Thickness _inset = new(0, ((MapMarkerLayout.Height + MapMarkerLayout.DiscBox) / 2) + 2, 0, 0);
    private bool _isVisible = true;

    /// <summary>A placement that never moves, for a marker built outside a map.</summary>
    public static MapNamePlacement Fixed { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The margin that puts the name in its slot inside the marker's own box.</summary>
    public Thickness Inset
    {
        get => _inset;
        set
        {
            if (_inset == value)
            {
                return;
            }

            _inset = value;
            PropertyChanged?.Invoke(this, new(nameof(Inset)));
        }
    }

    /// <summary>Whether this name found anywhere to go.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            PropertyChanged?.Invoke(this, new(nameof(IsVisible)));
        }
    }
}

/// <summary>
/// Another member of the group, drawn where they last said they were.
/// </summary>
/// <remarks>
/// Each member has a colour of their own, never cyan, so a glance never confuses somebody
/// else's last screenshot with your own and never confuses two of them with each other. The
/// same colour is the swatch beside their name in the panel, which is what joins the two.
///
/// Only members on the same map are ever drawn. Projecting a position from another map through
/// this map's transform would place them somewhere real-looking and entirely wrong.
/// </remarks>
/// <param name="Name">The name they chose.</param>
/// <param name="CenterX">Canvas position, already projected.</param>
/// <param name="CenterY">Canvas position, already projected.</param>
/// <param name="BearingDegrees">Their facing, converted into this map's frame.</param>
/// <param name="Detail">Name and age together, for the tooltip.</param>
/// <param name="IsStale">Whether their position is old enough that they have certainly moved.</param>
/// <summary>
/// A place the group marked, drawn on the map.
/// </summary>
/// <remarks>
/// Waypoints and pings share one shape because they are the same gesture at two speeds: both
/// are somebody saying "here". What differs is how long it means anything, so a ping is drawn
/// hollow and a waypoint filled, and a waypoint somebody has reached is drawn quiet rather than
/// removed, because "we went there" is worth keeping on screen.
/// </remarks>
/// <summary>Somebody asking that a place be marked for the group.</summary>
public sealed record GroupMarkRequest(string MapId, WorldPosition Position, bool IsPing);

public sealed record GroupMarkViewModel(
    long Id,
    double CenterX,
    double CenterY,
    string Label,
    string Detail,
    bool IsPing,
    bool IsReached)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    /// <summary>Whether this is the sender's own mark, drawn before the group has it back.</summary>
    /// <remarks>
    /// Reported as the gesture taking a second to do anything, which it did: every mark was
    /// drawn only once the server sent it back, so between the click and the next exchange the
    /// map showed nothing. Drawn hollow until the group confirms it, so "sent" and "landed"
    /// are still distinguishable.
    /// </remarks>
    public bool IsPending { get; init; }

    public double Extent => 46;

    public double Left => CenterX - (Extent / 2);

    public double Top => CenterY - (Extent / 2);

    public double PinSize => IsPing ? 14 : 19;

    /// <summary>
    /// Violet, which nothing else on this map is.
    /// </summary>
    /// <remarks>
    /// The group's marks were ochre, and so are scav extracts: reported as "they kind of blend
    /// in with scav extracts", which they did. The faction colours are spoken for -- cyan is
    /// PMC, ochre is scav, sage is shared -- so a mark that belongs to the group rather than to
    /// the map takes a hue none of them use.
    ///
    /// The shape changes too, never the colour alone. A waypoint is a diamond and a ping is a
    /// ring around a dot, so they are still told apart at the zoom where a marker is twelve
    /// pixels wide and by a player who cannot separate ochre from violet.
    /// </remarks>
    public string FillColor => IsPing
        ? "#FFFF5F8F"
        : IsPending ? "#40B98CFF" : IsReached ? "#66B98CFF" : "#FFB98CFF";

    public string OutlineColor => IsPing ? "#FFFFD0DE" : IsPending ? "#FFB98CFF" : "#FF0B1016";

    public double OutlineWidth => IsPing ? 2 : IsPending ? 2 : 1.5;

    /// <summary>The halo that makes a ping findable on a busy map.</summary>
    /// <remarks>
    /// A ping used to be a transparent disc with a thin outline, which is why it was reported
    /// as not showing up at all: it was drawn, and it looked like nothing. It is the one mark
    /// that says "look here right now" and it disappears after forty-five seconds, so it is
    /// the one mark allowed to be loud.
    /// </remarks>
    public bool HasHalo => IsPing;

    public double HaloSize => 34;

    public string HaloColor => "#59FF5F8F";

    public string HaloOutlineColor => "#B3FF5F8F";

    /// <summary>A diamond for a waypoint, so it is not a disc like everything else on the map.</summary>
    public bool IsDiamond => !IsPing;

    public double DiamondRotation => 45;

    public bool HasLabel => Label.Length > 0;

    public Thickness LabelInset => new(0, (Extent / 2) + 2, 0, 0);
}

/// <summary>
/// One other member of the group, written out beside the map rather than on it.
/// </summary>
/// <remarks>
/// A marker says where somebody is. It cannot say that they are on another map, that their
/// position is eight minutes old, or what they are carrying, and those are the things asked
/// out loud during a raid. The panel carries them, and only appears when somebody else is
/// actually there, so a player alone loses no width to it.
/// </remarks>
/// <param name="Name">Their chosen display name.</param>
/// <param name="Where">The map and what they are doing on it.</param>
/// <param name="Position">Where they were, and how long ago that was.</param>
/// <param name="Extra">Loadout and quests, where they share them.</param>
/// <param name="IsElsewhere">Whether they are on a map other than the one being looked at.</param>
/// <param name="IsStale">Whether their position is old enough to be treated as a guess.</param>
/// <summary>
/// One quest with something to do on the map being looked at.
/// </summary>
/// <remarks>
/// The map has drawn quest objectives for a while and the only list of them was a flat
/// per-objective dump in the expander at the bottom left, which is where the map explains
/// itself rather than where a player looks. This is the list beside the map: what is on it,
/// by quest, pinned first.
///
/// Only this map. A quest with nothing to do here is not on the map and has no business in
/// the column beside it.
/// </remarks>
/// <param name="Task">The quest's name.</param>
/// <param name="Objectives">What it wants done here, one line.</param>
/// <param name="IsPinned">Whether the player marked it as the one they are working on.</param>
/// <param name="IsApproximate">Whether nothing here is placed exactly, so the marks are a hint.</param>
public sealed record QuestPanelViewModel(
    string Task,
    string Objectives,
    bool IsPinned,
    bool IsApproximate);

/// <summary>
/// One place another player could have started this raid, beside the map.
/// </summary>
/// <remarks>
/// The first screenshot of a raid is taken where the player spawned, so the player spawn points
/// near it are where everybody else began. That is worth knowing for about ninety seconds and
/// then never again, which is why the panel is only on screen while a raid is running.
/// </remarks>
/// <param name="Name">What the map calls the spawn.</param>
/// <param name="FromStart">How far it is from where this raid began.</param>
/// <param name="FromPlayer">Where it lies from the player now, or nothing if they have not been seen.</param>
public sealed record SpawnPanelViewModel(string Name, string FromStart, string FromPlayer)
{
    public bool HasFromPlayer => FromPlayer.Length > 0;
}

/// <summary>One place near the player where the game spawns loot.</summary>
/// <param name="Name">What sort of thing it is, or what can be found in it.</param>
/// <param name="Where">How far and which way.</param>
public sealed record LootPanelViewModel(string Name, string Where);

public sealed record GroupMemberPanelViewModel(
    string Name,
    string Where,
    string Position,
    string Extra,
    bool IsElsewhere,
    bool IsStale)
{
    /// <summary>The same colour this member is drawn in on the map.</summary>
    public string Rgb { get; init; } = GroupMemberColors.Fallback;

    /// <summary>The swatch beside the name, which is what joins the row to the marker.</summary>
    public string SwatchColor => GroupMemberColors.WithAlpha(Rgb, "FF");

    public bool HasExtra => Extra.Length > 0;
}

/// <summary>Where one member of the group has been this raid, as a line on the map.</summary>
/// <remarks>
/// One dot says where somebody is. It does not say which way they came, whether they are
/// moving, or whether they have already swept the building you are about to walk into.
///
/// Drawn in that member's own colour and faded by the age of its oldest point, so a path
/// somebody walked five minutes ago is visibly not where they are now. Dashed, like the
/// player's own trail and for the same reason: the points are screenshots minutes apart, and a
/// solid line between two of them would claim a route that was never observed.
/// </remarks>
public sealed record GroupTrailViewModel(string Name, AvaloniaList<Point> Points, TimeSpan OldestAge)
{
    /// <summary>That member's colour, which is also their dot and their row in the panel.</summary>
    public string Rgb { get; init; } = GroupMemberColors.Fallback;

    /// <summary>
    /// Faded by age rather than drawn flat.
    /// </summary>
    /// <remarks>
    /// A member who took three screenshots in ten seconds and then none for five minutes must
    /// not draw a line implying they walked it recently. Full strength for the first minute,
    /// down to a hint at five.
    /// </remarks>
    public string StrokeColor => GroupMemberColors.WithAlpha(Rgb, Alpha);

    private string Alpha => OldestAge switch
    {
        var age when age <= TimeSpan.FromMinutes(1) => "B0",
        var age when age <= TimeSpan.FromMinutes(3) => "70",
        var age when age <= TimeSpan.FromMinutes(5) => "40",
        _ => "22",
    };

    public bool HasPath => Points.Count > 1;
}

/// <summary>
/// A place another player started this raid, drawn on the map with a line to you.
/// </summary>
/// <remarks>
/// A list says "40 m NE"; a line says which way without reading anything, and in the first
/// ninety seconds of a raid that is the difference. It fades with the raid, because where
/// everybody spawned stops being where everybody is.
/// </remarks>
public sealed record SpawnThreatViewModel(
    string Name,
    double CenterX,
    double CenterY,
    AvaloniaList<Point> LineToPlayer,
    double Strength)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    public double Extent => 30;

    public double Left => CenterX - (Extent / 2);

    public double Top => CenterY - (Extent / 2);

    public double PinSize => 12;

    /// <summary>
    /// Red, which on this map means only this.
    /// </summary>
    /// <remarks>
    /// Nothing else here is red: extracts are cyan, ochre and sage by faction, group marks are
    /// violet, the player is cyan. A place somebody else may be standing right now is the one
    /// thing on the map worth a colour of its own.
    /// </remarks>
    public string FillColor => Alpha("#FFE05C5C", Strength);

    public string LineColor => Alpha("#FFE05C5C", Strength * 0.55);

    public string OutlineColor => "#FF0B1016";

    public bool HasLine => LineToPlayer.Count > 1;

    private static string Alpha(string rgb, double strength)
    {
        var value = (int)Math.Round(Math.Clamp(strength, 0, 1) * 255);
        return "#" + value.ToString("X2", CultureInfo.InvariantCulture) + rgb[3..];
    }
}

/// <summary>One floor of a map drawn in the stacked view.</summary>
/// <param name="Name">What the floor is called, for the tooltip.</param>
/// <param name="Image">Its artwork, the same picture the flat view draws.</param>
/// <param name="Offset">
/// How far up the canvas it sits, measured from the floor being read rather than from the
/// lowest one. That puts the chosen floor at zero, which is where every marker already is, so
/// the markers line up with the floor they describe without a single overlay being moved.
/// </param>
/// <param name="Opacity">Solid for the floor being read, faint for the rest.</param>
public sealed record FloorLayerViewModel(
    string Name,
    Bitmap Image,
    double Offset,
    double Opacity)
{
    /// <summary>Negative, because up the screen is a smaller Y.</summary>
    public double Translate => -Offset;
}

public sealed record GroupMarkerViewModel(
    string Name,
    double CenterX,
    double CenterY,
    double BearingDegrees,
    string Detail,
    bool IsStale)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    public double Extent => 46;

    public double Left => CenterX - (Extent / 2);

    public double Top => CenterY - (Extent / 2);

    public double DotSize => 13;

    /// <summary>This member's own colour, which the panel beside the map shows as well.</summary>
    /// <remarks>
    /// Every member used to be the same ochre, so three of them on one map said where three
    /// people were without saying which was which.
    /// </remarks>
    public string Rgb { get; init; } = GroupMemberColors.Fallback;

    public string FillColor => GroupMemberColors.WithAlpha(Rgb, IsStale ? "80" : "FF");

    public string OutlineColor => "#FF0B1016";

    public string ConeColor => GroupMemberColors.WithAlpha(Rgb, IsStale ? "38" : "60");

    public string ConeGeometry => "M 23,23 L 10,4 A 17,17 0 0 1 36,4 Z";
}

/// <summary>What a feature marker stands for, which decides its shape, colour and glyph.</summary>
public enum MapMarkerKind
{
    Extract,
    Transit,
    Spawn,
    Lock,
}

/// <summary>
/// The fixed box every feature marker is drawn in, in screen pixels.
/// </summary>
/// <remarks>
/// A marker is a disc with a name under it, and the name is often wider than the disc. Layout
/// clips a child to its slot rather than letting it overflow, so the box has to be wide enough
/// for the longest name and tall enough for the name to sit clear of the disc. The disc is at
/// the exact centre so that the whole box can be scaled and positioned about one point, which
/// is the feature's position.
/// </remarks>
internal static class MapMarkerLayout
{
    public const double Width = 240;

    /// <summary>
    /// The box a marker and its name share, tall enough for a name two rows either side.
    /// </summary>
    /// <remarks>
    /// The box has no background and catches no pointer, so its only job is to be large enough
    /// to hold what is drawn in it. It was 84, which held one name directly under the disc;
    /// names that have to step a row out of somebody else's way need the room, and a child
    /// arranged past the bottom edge is squashed to whatever is left rather than overflowing.
    /// </remarks>
    public const double Height = 140;

    /// <summary>The square the disc, its halo and its glyph share, centred in the box.</summary>
    public const double DiscBox = 34;
}

public sealed record MapOverlayViewModel(MapOverlayKind Kind, string Name, bool IsVisible, bool IsHighlighted, int Count)
{
    public string HighlightLabel => IsHighlighted ? "Highlighted" : "Highlight";

    /// <summary>The catalog calls them labels; a player calls them place names.</summary>
    public string DisplayName => Kind == MapOverlayKind.Labels ? "Place names" : Name;

    /// <summary>
    /// Whether the layer earns a row in the list.
    /// </summary>
    /// <remarks>
    /// The render model carries layers for routes, traffic and filters that nothing yet draws.
    /// A toggle that changes nothing teaches the player that toggles change nothing, so a layer
    /// is listed only when it has something on it. Quest objectives are the exception: their
    /// geometry lives outside this count and turning the layer on is what fetches it.
    /// </remarks>
    public bool IsListed => Kind == MapOverlayKind.QuestObjectives || Count > 0;

    /// <summary>Extracts first, because they are what the map is for; place names last.</summary>
    public int Rank => Kind switch
    {
        MapOverlayKind.Extracts => 0,
        MapOverlayKind.QuestObjectives => 1,
        MapOverlayKind.Keys => 2,
        MapOverlayKind.Spawns => 3,
        MapOverlayKind.Labels => 4,
        _ => 5,
    };

    public string CountText => Count > 0 ? Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public bool IsExtracts => Kind == MapOverlayKind.Extracts;

    public bool IsSpawns => Kind == MapOverlayKind.Spawns;

    public bool IsKeys => Kind == MapOverlayKind.Keys;

    public bool IsLabels => Kind == MapOverlayKind.Labels;

    public bool IsQuestObjectives => Kind == MapOverlayKind.QuestObjectives;

    /// <summary>Whether the row's swatch is a disc drawn like the markers, so the list is also the key.</summary>
    public bool HasDiscSwatch => Kind is MapOverlayKind.Extracts or MapOverlayKind.Spawns or MapOverlayKind.Keys or MapOverlayKind.QuestObjectives;
}

/// <summary>
/// One fixed feature on the map: an extract, a transit, a spawn or a locked door.
/// </summary>
/// <remarks>
/// Colour, size and shape belong to the styles and are chosen there by the kind flags; this
/// carries only what the styles cannot decide, which is where the marker goes, what it says
/// and which kind it is. The box is positioned by its corner and scaled about its centre, so
/// the centre is what has to land on the feature.
/// </remarks>
/// <param name="Name">The feature's own label, without any mark the map adds to it.</param>
/// <param name="IsDimmed">Whether another layer is being highlighted, so this one recedes.</param>
/// <param name="IsOffered">Whether this is an extract the player was actually offered this raid.</param>
public sealed record MapOverlayElementViewModel(
    string Name,
    double CenterX,
    double CenterY,
    MapMarkerKind Kind,
    bool IsHighlighted,
    bool IsDimmed,
    bool IsOffered)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    /// <summary>Where this marker's name sits, decided against every other name on the map.</summary>
    public MapNamePlacement Placement { get; init; } = MapNamePlacement.Fixed;

    /// <summary>What this asks of you: a switch, a payment, a side.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Which side this feature is for, where the data says.
    /// </summary>
    /// <remarks>
    /// Reported as "it doesn't distinguish scav vs player extracts and spawns easily at all,
    /// they are all the same color and symbol". They were: the feed has carried this all along
    /// and the whole App project contained no reference to it.
    ///
    /// Both the colour and the glyph change, never colour alone. Colour alone fails for a
    /// colourblind player, and it fails for everybody at the zoom where a marker is twelve
    /// pixels across, which is most of the time.
    /// </remarks>
    public MapFeatureFaction Faction { get; init; } = MapFeatureFaction.Unknown;

    public bool IsPmc => Faction == MapFeatureFaction.Pmc;

    public bool IsScav => Faction == MapFeatureFaction.Scav;

    public bool IsShared => Faction == MapFeatureFaction.Shared;

    /// <summary>The name as drawn, starred when it is one the player can use now.</summary>
    public string Label => IsOffered ? "★ " + Name : Name;

    public double Width => MapMarkerLayout.Width;

    public double Height => MapMarkerLayout.Height;

    public double DiscBox => MapMarkerLayout.DiscBox;

    public double Left => CenterX - (Width / 2);

    public double Top => CenterY - (Height / 2);

    /// <summary>How wide this name reads on screen, close enough to arrange by.</summary>
    /// <remarks>
    /// An estimate rather than a measurement. The arrangement is decided before anything is
    /// laid out, and asking the layout for a width it has not computed yet would mean
    /// arranging the names one frame behind the map. Eleven pixel text averages a little under
    /// six pixels a character; the estimate is generous, so a name that fits is a name that
    /// really fits, and the cost of being wrong is a slightly emptier map rather than two
    /// names on top of each other. The cap is the width the style itself imposes.
    /// </remarks>
    public double EstimatedNameWidth => Math.Min(220, 10 + (Label.Length * 5.9));

    /// <summary>How tall it reads: eleven pixel text with a pixel of padding each side.</summary>
    public const double NameHeight = 17;

    public bool IsExtract => Kind == MapMarkerKind.Extract;

    public bool IsTransit => Kind == MapMarkerKind.Transit;

    public bool IsSpawn => Kind == MapMarkerKind.Spawn;

    public bool IsLock => Kind == MapMarkerKind.Lock;

    /// <summary>
    /// The outline drawn on the disc, in a twelve pixel box.
    /// </summary>
    /// <remarks>
    /// Drawn as strokes rather than filled shapes so that one path can carry a doorway and an
    /// arrow, or a shackle and a body, without the open parts being filled in as if closed. A
    /// spawn is a plain dot: it is context rather than a target, and a glyph would make it
    /// compete with the exits.
    /// </remarks>
    public string? Glyph => Kind switch
    {
        // A doorway with an arrow through it, and the arrow's tail says who may use it: one
        // stroke for a PMC exit, two for a scav one, a full bar for an exit either side can
        // take. That reads at twelve pixels and it survives being printed in grey.
        MapMarkerKind.Extract => Faction switch
        {
            MapFeatureFaction.Scav => "M 4,1.5 H 1.5 V 10.5 H 4 M 4.5,6 H 11 M 8.5,3.5 L 11,6 L 8.5,8.5 M 5.5,4 V 8 M 7,4 V 8",
            MapFeatureFaction.Shared => "M 4,1.5 H 1.5 V 10.5 H 4 M 4.5,6 H 11 M 8.5,3.5 L 11,6 L 8.5,8.5 M 5.5,3.5 V 8.5",
            _ => "M 4,1.5 H 1.5 V 10.5 H 4 M 4.5,6 H 11 M 8.5,3.5 L 11,6 L 8.5,8.5",
        },
        MapMarkerKind.Transit => "M 2.5,2.5 L 6,6 L 2.5,9.5 M 6.5,2.5 L 10,6 L 6.5,9.5",
        MapMarkerKind.Lock => "M 3.5,5.5 V 4 A 2.5,2.5 0 0 1 8.5,4 V 5.5 M 2,5.5 H 10 V 10.5 H 2 Z",
        // A spawn is context rather than a target, so it stays a plain dot and says who it is
        // for with colour only. A shared spawn gets a ring, because "either side starts here"
        // is worth seeing before a raid.
        MapMarkerKind.Spawn when Faction == MapFeatureFaction.Shared => "M 6,2.5 A 3.5,3.5 0 1 1 5.99,2.5 Z",
        _ => null,
    };

    public bool HasGlyph => Glyph is not null;

    /// <summary>
    /// Whether the name waits for the pointer rather than sitting on the map.
    /// </summary>
    /// <remarks>
    /// An extract's name is the answer to "which one is that"; a spawn's or a door's is detail
    /// that fifty markers' worth of text would bury the map under. Those show their name when
    /// pointed at.
    /// </remarks>
    public bool IsNameQuiet => Kind is MapMarkerKind.Spawn or MapMarkerKind.Lock;

    public string KindName => (Kind, Faction) switch
    {
        (MapMarkerKind.Extract, MapFeatureFaction.Pmc) => "PMC extract",
        (MapMarkerKind.Extract, MapFeatureFaction.Scav) => "Scav extract",
        (MapMarkerKind.Extract, MapFeatureFaction.Shared) => "Extract, either side",
        (MapMarkerKind.Extract, _) => "Extract",
        (MapMarkerKind.Transit, _) => "Transit to another map",
        (MapMarkerKind.Spawn, MapFeatureFaction.Pmc) => "PMC spawn",
        (MapMarkerKind.Spawn, MapFeatureFaction.Scav) => "Scav spawn",
        (MapMarkerKind.Spawn, MapFeatureFaction.Shared) => "Spawn, either side",
        (MapMarkerKind.Spawn, _) => "Spawn",
        _ => "Locked door",
    };

    /// <summary>
    /// Whether two markers stand for the same feature, across a rebuild of the list.
    /// </summary>
    /// <remarks>
    /// Compared on kind, name and position rather than on the record, because the record also
    /// carries highlight and offered state, and a selection should survive a scan turning the
    /// selected extract into an offered one.
    /// </remarks>
    public bool IsSameFeatureAs(MapOverlayElementViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            CenterX.Equals(other.CenterX) &&
            CenterY.Equals(other.CenterY);
    }
}

/// <summary>
/// A place name printed on the map, from the catalog's own label list.
/// </summary>
/// <remarks>
/// Drawn as lettering rather than as a chip: a boxed label reads as a control, and sixty of
/// them read as a form laid over the map. The box here is generous and centred on the point so
/// the text can be rotated the way the catalog asks without being cut by its own bounds.
/// </remarks>
public sealed record MapPlaceNameViewModel(
    string Text,
    double CenterX,
    double CenterY,
    double RotationDegrees,
    double FontSize,
    bool IsHighlighted,
    bool IsDimmed)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    public double Width => 400;

    public double Height => 48;

    public double Left => CenterX - (Width / 2) + Nudge;

    public double Top => CenterY - (Height / 2);

    /// <summary>
    /// How wide the text itself reads, rather than the box it is centred in.
    /// </summary>
    /// <remarks>
    /// <see cref="Width"/> is 400 because the box has to be wide enough for the longest name
    /// and the text is centred in it; almost all of that is empty. Anything asking "what does
    /// this cover" needs the text, and treating the box as covered ground would blank out most
    /// of the map's other names.
    ///
    /// Estimated from the character count the same way a marker's name is, because measuring
    /// requires a laid-out text run and this is decided before layout.
    /// </remarks>
    public double TextWidth => Math.Min(Width, 8 + (Text.Length * FontSize * 0.55));

    public double TextHeight => FontSize * 1.35;

    public double TextLeft => CenterX - (TextWidth / 2) + Nudge;

    public double TextTop => CenterY - (TextHeight / 2);

    /// <summary>How wide the canvas is, so a name at the edge of it can be kept inside.</summary>
    public double CanvasWidth { get; init; }

    /// <summary>
    /// The map's zoom when this name was built.
    /// </summary>
    /// <remarks>
    /// Carried on the record rather than read off the live <see cref="Scale"/>, and that is the
    /// whole fix rather than a detail.
    ///
    /// This is a record, so two of them with equal values are equal. An items control given a
    /// list of equal items reuses the containers it already has and never re-reads a computed
    /// property, so <see cref="Nudge"/> reading a live scale object was evaluated once, at
    /// whatever zoom applied when the map first drew, and never again. Measured: the label
    /// strip was pixel-identical across the build that changed the arithmetic, because the
    /// arithmetic was never run a second time.
    ///
    /// Held here, a name at one zoom is a different value from the same name at another, the
    /// containers are rebuilt, and the nudge is recomputed. It is the same lesson as
    /// <c>CanStack</c>: a value that depends on something that changes has to be announced, and
    /// for a record the announcement is being a different record.
    /// </remarks>
    public double Zoom { get; init; } = 1;

    /// <summary>
    /// How wide the text is in canvas units rather than on screen.
    /// </summary>
    /// <remarks>
    /// The two are not the same number and mixing them is the mistake this file has now made
    /// three times. A place name is counter-scaled, so <see cref="TextWidth"/> is its size on
    /// screen and never changes; the position it is compared against is in canvas units, which
    /// at 23% zoom are more than four times smaller. Anything comparing a size with a position
    /// has to convert one of them first, and the zoom that does the converting is
    /// <see cref="Zoom"/>, held on the record so it cannot go stale.
    /// </remarks>
    public double TextWidthOnCanvas => Zoom > 0 ? TextWidth / Zoom : TextWidth;

    /// <summary>
    /// How far along the canvas a name has to move to stay on the map, in canvas units.
    /// </summary>
    /// <remarks>
    /// Reported as two labels drawn on top of each other, which is what it looks like: on
    /// Customs, "ministration Gate" beside "ry Checkpoint", both starting exactly at the map's
    /// left edge. They are not overlapping. They are "Administration Gate" and "Factory
    /// Checkpoint", each with its first few characters cut off by the canvas, landing on the
    /// same row and reading as one illegible run.
    ///
    /// The catalog puts a label at the point it names, and a point near the edge of the map has
    /// half its name past the edge. The canvas clips its children, so the half outside is
    /// simply gone.
    ///
    /// Moved rather than dropped, and only as far as it takes. A name a few pixels inboard
    /// still names the thing it is beside; half a name names nothing. Nothing moves at all
    /// unless it would otherwise be cut, so a label in the middle of the map is untouched.
    /// </remarks>
    public double Nudge
    {
        get
        {
            var half = TextWidthOnCanvas / 2;
            if (CenterX - half < 0)
            {
                return half - CenterX;
            }

            var overhang = CenterX + half - CanvasWidth;
            return CanvasWidth > 0 && overhang > 0 ? -overhang : 0;
        }
    }
}

/// <summary>
/// The ring and callout drawn on the marker the player clicked.
/// </summary>
/// <remarks>
/// A separate layer rather than a state on the marker, so selecting one does not rebuild the
/// hundred others. The leader runs up and to the right and the card hangs off its end, which
/// keeps the card clear of the marker's own name below the disc.
/// </remarks>
public sealed record MapMarkerSelectionViewModel(
    string Title,
    string Subtitle,
    double CenterX,
    double CenterY)
{
    private const double LeaderRun = 28;

    /// <summary>How wide the card may be, which is what has to fit beside the marker.</summary>
    private const double CardWidth = 260;

    private const double CardHeight = 120;

    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    /// <summary>
    /// What this exit asks of you, where the feed says anything.
    /// </summary>
    /// <remarks>
    /// Asked for as "clicking an extract should pull up a picture of it and maybe any
    /// important instructions for it too". The picture has no source; this is the
    /// instructions, and they were in the synced payload the whole time.
    /// </remarks>
    public string? Conditions { get; init; }

    public bool HasConditions => !string.IsNullOrWhiteSpace(Conditions);

    /// <summary>The canvas this is drawn on, so the card can stay inside it.</summary>
    /// <remarks>
    /// The callout always went up and to the right, so on a marker near the right edge it ran
    /// off the map and was clipped by the surface, which is what somebody reported: the popup
    /// cut off by the bound of the map. Knowing how much room there is turns that into a
    /// choice of side.
    /// </remarks>
    public double CanvasWidth { get; init; }

    public double CanvasHeight { get; init; }

    public double Width => 640;

    public double Height => 300;

    public double Left => CenterX - (Width / 2);

    public double Top => CenterY - (Height / 2);

    /// <summary>
    /// Whether the card goes left of the marker instead of right.
    /// </summary>
    /// <remarks>
    /// Flips when there is not room on the usual side. The card is drawn at the map's own
    /// scale, so the room it needs shrinks as the map is zoomed in, which is why this is
    /// measured against the scaled width rather than a constant.
    /// </remarks>
    public bool PrefersLeft =>
        CanvasWidth > 0 && CenterX + ((LeaderRun + CardWidth) * Scale.Inverse) > CanvasWidth;

    /// <summary>Whether the card goes below the marker instead of above.</summary>
    public bool PrefersDown =>
        CanvasHeight > 0 && CenterY - ((LeaderRun + CardHeight) * Scale.Inverse) < 0;

    public Point LeaderStart => new(Width / 2, Height / 2);

    public Point LeaderEnd => new(
        (Width / 2) + (PrefersLeft ? -LeaderRun : LeaderRun),
        (Height / 2) + (PrefersDown ? LeaderRun : -LeaderRun));

    /// <summary>
    /// Puts the card's corner on the end of the leader, on whichever side has room.
    /// </summary>
    /// <remarks>
    /// A margin only pins the edges it sets, so each side is either an offset from the centre
    /// or zero, and the opposite pair does the pinning when the card flips.
    /// </remarks>
    public Thickness CardInset => new(
        PrefersLeft ? 0 : (Width / 2) + LeaderRun,
        PrefersDown ? (Height / 2) + LeaderRun : 0,
        PrefersLeft ? (Width / 2) + LeaderRun : 0,
        PrefersDown ? 0 : (Height / 2) + LeaderRun);
}

public sealed record QuestMapPointViewModel(
    string Label,
    double CenterX,
    double CenterY,
    bool IsPinned,
    bool IsHighlighted)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    public double Size => IsHighlighted ? IsPinned ? 24 : 22 : IsPinned ? 18 : 14;

    public double Left => CenterX - (Size / 2);

    public double Top => CenterY - (Size / 2);

    public double BorderThickness => IsHighlighted ? IsPinned ? 4 : 3 : IsPinned ? 3 : 2;

    public string FillColor => IsHighlighted ? "#FFF0B44D" : IsPinned ? "#FFC6A15B" : "#E656B8C6";

    public string BorderColor => IsPinned ? "#FFFFFFFF" : "#FFE6EDF2";
}

public sealed record QuestMapRegionViewModel(
    string Label,
    AvaloniaList<Point> Points,
    bool IsPinned,
    bool IsHighlighted)
{
    public string FillColor => IsHighlighted ? "#70F0B44D" : IsPinned ? "#50C6A15B" : "#3056B8C6";

    public string StrokeColor => IsHighlighted ? "#FFF0B44D" : IsPinned ? "#FFFFFFFF" : "#E6C6A15B";

    public double StrokeThickness => IsHighlighted ? IsPinned ? 5 : 4 : IsPinned ? 3 : 2;
}

public static class MapCanvasCoordinateMapper
{
    /// <summary>
    /// How many upstream PNG tiles one view may stitch together.
    /// </summary>
    /// <remarks>
    /// This was pinned at 16 here while the planner's own default is 64, so any map whose
    /// upstream bounds need more than a four-by-four grid rendered nothing at all. Customs
    /// needs twenty.
    /// </remarks>
    /// <summary>
    /// How many tiles one map may hold at once.
    /// </summary>
    /// <remarks>
    /// This was sixteen, then sixty-four because Customs needs twenty at its coarsest level,
    /// and it is now the budget that decides how sharp a map can be rather than merely whether
    /// it renders at all. Each step up the pyramid doubles the resolution and quadruples the
    /// count, so sixty-four could not afford a single step for most maps and every map was
    /// drawn at the blurriest level it publishes.
    ///
    /// Two hundred and fifty-six tiles of 256 pixels is about 67 MB of decoded bitmap, which
    /// is affordable for one map on a desktop and buys two whole levels, four times the linear
    /// detail. That is the difference between Factory reading as a flat brown mass and reading
    /// as a floor plan.
    /// </remarks>
    public const int MaximumTilesPerView = 256;

    /// <summary>
    /// Picks the sharpest pyramid level that fits the tile budget.
    /// </summary>
    /// <remarks>
    /// Highest first, because the point is detail; the loop stops at the first level that
    /// fits, and falls back to the minimum so a map that fits nothing still draws.
    /// </remarks>
    public static int ChooseTileZoom(MapVariant variant, int budget = MaximumTilesPerView)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var minimum = variant.MinimumZoom ?? 0;
        var maximum = variant.MaximumZoom ?? minimum;
        for (var zoom = maximum; zoom > minimum; zoom--)
        {
            var plan = MapTilePlanner.Plan(variant, zoom, budget);
            if (plan.IsValid)
            {
                return zoom;
            }
        }

        return minimum;
    }

    public static Func<MapPoint, Point>? Create(
        MapRenderModel renderModel,
        double canvasWidth,
        double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(renderModel);
        var variant = renderModel.Variant;
        if (variant.Transform is null ||
            !double.IsFinite(canvasWidth) || canvasWidth <= 0 ||
            !double.IsFinite(canvasHeight) || canvasHeight <= 0)
        {
            return null;
        }

        // The level the loader actually fetched, not the pyramid's minimum. These must be the
        // same number or every marker lands in a different coordinate space from the artwork.
        if (renderModel.Background?.Kind == MapBackgroundKind.TileTemplate
            && (renderModel.Background.TileZoom ?? variant.MinimumZoom) is { } zoom)
        {
            var plan = MapTilePlanner.Plan(variant, zoom, MaximumTilesPerView);
            if (plan.IsValid)
            {
                var scale = Math.Pow(2, zoom);
                return point => new(
                    (point.X * scale) - plan.OriginPixelX,
                    (point.Y * scale) - plan.OriginPixelY);
            }
        }

        if (variant.Bounds?.IsValid != true)
        {
            return null;
        }

        var bounds = variant.SvgBounds ?? variant.Bounds;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = corners.Select(corner =>
        {
            variant.Transform.TryProject(corner, out var point);
            return point;
        }).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        if (maximumX <= minimumX || maximumY <= minimumY)
        {
            return null;
        }

        return point => new(
            ((point.X - minimumX) / (maximumX - minimumX)) * canvasWidth,
            ((point.Y - minimumY) / (maximumY - minimumY)) * canvasHeight);
    }
}

/// <summary>
/// Where the player was when they last took a screenshot, drawn on the map.
/// </summary>
/// <remarks>
/// This is the one thing the companion can say about the player's own location, and it is
/// evidence rather than tracking: the game writes the position into the screenshot's filename,
/// and only when the player chooses to take one. The marker is therefore always labelled with
/// how old it is, and it points the way the player was facing at that moment.
///
/// Everything is laid out inside a fixed square centred on the position, and the whole square
/// is rotated about its own centre. An earlier version rotated the facing arrow about its own
/// bottom edge while the arrow sat above the dot, so it spun on the spot instead of swinging
/// around the player, which is why the direction looked wrong or absent.
/// </remarks>
/// <param name="Label">What the marker is and when it was taken, for the tooltip.</param>
/// <param name="CenterX">Canvas position, already projected through the map's transform.</param>
/// <param name="CenterY">Canvas position, already projected through the map's transform.</param>
/// <param name="BearingDegrees">
/// Which way the player was facing, in the map's own frame rather than the world's, clockwise
/// from the top of the map.
/// </param>
/// <param name="IsStale">Whether the screenshot is old enough that the player has likely moved.</param>
public sealed record PlayerMarkerViewModel(
    string Label,
    double CenterX,
    double CenterY,
    double BearingDegrees,
    bool IsStale)
{
    public MapMarkerScale Scale { get; init; } = MapMarkerScale.Unscaled;

    /// <summary>The square the whole marker is drawn inside, big enough for the facing cone.</summary>
    public double Extent => 52;

    public double Left => CenterX - (Extent / 2);

    public double Top => CenterY - (Extent / 2);

    /// <summary>The dot itself, drawn as an ellipse so it is round whatever the theme does.</summary>
    /// <remarks>
    /// This was a bordered rectangle with its corner radius bound from a number, which does
    /// not convert, so it rendered as a square. An ellipse cannot be anything but round.
    /// </remarks>
    public double DotSize => 15;

    /// <summary>
    /// The facing cone, drawn from the centre of the square pointing straight up.
    /// </summary>
    /// <remarks>
    /// A cone rather than an arrow. On a busy satellite map a thin arrow disappears into the
    /// detail, and a cone reads as "looking that way" at a glance while still being legible
    /// when the marker is small.
    /// </remarks>
    public string ConeGeometry => "M 26,26 L 11,4 A 19,19 0 0 1 41,4 Z";

    /// <summary>A fresh position is worth trusting; a stale one is drawn as a faded hint.</summary>
    public string FillColor => IsStale ? "#8056B8C6" : "#FF34D3E8";

    /// <summary>
    /// The outline, which is what keeps the marker visible on light and dark artwork alike.
    /// </summary>
    /// <remarks>
    /// A white marker vanishes over pale ground and a dark one vanishes over shadow, which is
    /// why it could not always be seen. A dark ring around a bright fill reads on both.
    /// </remarks>
    public string OutlineColor => "#FF0B1016";

    public string ConeColor => IsStale ? "#4034D3E8" : "#7034D3E8";

    public double DotBorderThickness => 2.5;
}

public sealed record QuestMapAssociationViewModel(
    string Title,
    string Detail,
    string Evidence,
    string Items,
    bool HasExactGeometry,
    bool IsUnsupported,
    bool IsFloorFiltered);

public sealed class MapViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly HttpClient? _ownedHttpClient;
    private readonly TarkovDevMapCatalogClient _catalogClient;
    private readonly TarkovDevMapAssetCache _assetCache;
    private readonly MapVariantSelectionService _selectionService;
    private readonly IPlayerProfileService? _profileService;
    private readonly IMapFeatureCatalog? _featureCatalog;
    private readonly IQuestReadService? _questReadService;
    private readonly QuestMapProjectionService? _questProjectionService;
    private readonly MapPresentationService _presentationService = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selectionLoad;
    private IReadOnlyList<MapLocation> _locations = [];
    private IReadOnlyList<MapVariant> _variants = [];
    private IReadOnlyList<MapFloorDefinition> _floors = [];
    private IReadOnlyList<MapOverlayViewModel> _overlays = [];
    private IReadOnlyList<MapPlaceNameViewModel> _placeNames = [];
    private IReadOnlyList<MapOverlayElementViewModel> _markers = [];
    private IReadOnlyList<MapMarkerSelectionViewModel> _selectedMarkers = [];
    private MapOverlayElementViewModel? _selectedMarker;
    private readonly MapMarkerScale _markerScale = new();
    private IReadOnlyList<MapTileViewModel> _tiles = [];
    private IReadOnlyList<QuestMapPointViewModel> _questPoints = [];
    private IReadOnlyList<QuestMapRegionViewModel> _questRegions = [];
    private IReadOnlyList<QuestMapAssociationViewModel> _questAssociations = [];
    private MapLocation? _selectedLocation;
    private MapVariant? _selectedVariant;
    private MapFloorDefinition? _selectedFloor;
    private MapRenderModel? _renderModel;
    private PixelRect _backgroundDrawnPixels;
    private PixelSize _backgroundPixelSize;
    private ScreenshotPosition? _playerPosition;
    private IReadOnlyList<ScreenshotPosition> _playerTrailPositions = [];
    private MapFeatureFaction _side = MapFeatureFaction.Unknown;
    private IReadOnlyList<ActiveExtract> _activeExtracts = [];
    private IReadOnlyList<GroupMemberView> _groupMembers = [];
    private IReadOnlyList<GroupMemberPanelViewModel> _groupPanel = [];
    private IReadOnlyList<SpawnPanelViewModel> _spawnPanel = [];
    private IReadOnlyList<LootPanelViewModel> _lootPanel = [];
    private IReadOnlyList<SpawnThreatViewModel> _spawnThreats = [];
    private IReadOnlyList<FloorLayerViewModel> _floorLayers = [];
    private bool _isStacked;
    private string _spawnPanelDetail = string.Empty;
    private IReadOnlyList<MapFeature> _mapFeatures = [];
    private IReadOnlyList<QuestPanelViewModel> _questPanel = [];
    private IReadOnlyDictionary<string, string> _groupColors =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyList<GroupMarkerViewModel> _groupMarkers = [];
    private IReadOnlyList<GroupTrailViewModel> _groupTrails = [];
    private IReadOnlyList<GroupMarkViewModel> _groupMarks = [];
    private IReadOnlyList<GroupWaypointView> _waypoints = [];
    private IReadOnlyList<GroupPingView> _pings = [];
    private AvaloniaList<Point> _playerTrail = [];
    private string? _followedPositionFilename;
    private bool _followsPlayer = true;
    private bool _prefersDrawing;
    private bool _autoSelectsFloor = true;
    private bool _isLoadingVariant;
    private string? _flooredPositionFilename;
    private bool _hasArtworkChoice;
    private IReadOnlyList<PlayerMarkerViewModel> _playerMarkers = [];
    private MapCatalogProvenance? _mapCatalogProvenance;
    private QuestMapProjectionReadModel? _questProjection;
    private Bitmap? _backgroundImage;
    private string _status = "Loading the tarkov.dev map catalog…";
    private string _questLayerStatus = "Quest layer is off";
    private long _questRefreshGeneration;
    private double _canvasWidth = 900;
    private double _canvasHeight = 620;
    private double _zoomScale = 1;
    private bool _isAutoFit = true;
    private bool _disposed;

    public MapViewModel(
        TarkovDevMapCatalogClient catalogClient,
        TarkovDevMapAssetCache assetCache,
        MapVariantSelectionService selectionService,
        IPlayerProfileService profileService,
        IQuestReadService questReadService,
        QuestMapProjectionService questProjectionService,
        IMapFeatureCatalog? featureCatalog = null)
        : this(
            null,
            catalogClient,
            assetCache,
            selectionService,
            profileService,
            questReadService,
            questProjectionService,
            featureCatalog)
    {
    }

    private MapViewModel(
        HttpClient? ownedHttpClient,
        TarkovDevMapCatalogClient catalogClient,
        TarkovDevMapAssetCache assetCache,
        MapVariantSelectionService selectionService,
        IPlayerProfileService? profileService,
        IQuestReadService? questReadService,
        QuestMapProjectionService? questProjectionService,
        IMapFeatureCatalog? featureCatalog = null)
    {
        _ownedHttpClient = ownedHttpClient;
        _featureCatalog = featureCatalog;
        _catalogClient = catalogClient;
        _assetCache = assetCache;
        _selectionService = selectionService;
        _profileService = profileService;
        _questReadService = questReadService;
        _questProjectionService = questProjectionService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<MapLocation> Locations
    {
        get => _locations;
        private set => Set(ref _locations, value);
    }

    public IReadOnlyList<MapVariant> Variants
    {
        get => _variants;
        private set => Set(ref _variants, value);
    }

    public IReadOnlyList<MapFloorDefinition> Floors
    {
        get => _floors;
        private set
        {
            Set(ref _floors, value);
            // Whether a map can be stacked is a fact about its floors, so it has to be restated
            // when they arrive. Without this the toggle is evaluated once, against an empty
            // list, and never appears on any map: the feature is built, bound, and invisible.
            OnPropertyChanged(nameof(CanStack));
        }
    }

    public IReadOnlyList<MapOverlayViewModel> Overlays
    {
        get => _overlays;
        private set => Set(ref _overlays, value);
    }

    public IReadOnlyList<MapPlaceNameViewModel> PlaceNames
    {
        get => _placeNames;
        private set => Set(ref _placeNames, value);
    }

    /// <summary>The extracts, transits, spawns and locked doors on the visible layers.</summary>
    public IReadOnlyList<MapOverlayElementViewModel> Markers
    {
        get => _markers;
        private set => Set(ref _markers, value);
    }

    /// <summary>
    /// The marker the player clicked, as a list of at most one.
    /// </summary>
    /// <remarks>
    /// A list for the same reason the player marker is one: the layer is bound like every
    /// other and disappears on its own when nothing is selected, with no null to guard in the
    /// view.
    /// </remarks>
    public IReadOnlyList<MapMarkerSelectionViewModel> SelectedMarkers
    {
        get => _selectedMarkers;
        private set => Set(ref _selectedMarkers, value);
    }

    public IReadOnlyList<MapTileViewModel> Tiles
    {
        get => _tiles;
        private set
        {
            var replaced = _tiles;
            Set(ref _tiles, value);
            UpdateContentBounds();
            OnPropertyChanged(nameof(HasTiles));
            OnPropertyChanged(nameof(ShowsPlaceholder));
            ReleaseLater(replaced.Select(tile => tile.Image));
        }
    }

    public IReadOnlyList<QuestMapPointViewModel> QuestPoints
    {
        get => _questPoints;
        private set => Set(ref _questPoints, value);
    }

    public IReadOnlyList<QuestMapRegionViewModel> QuestRegions
    {
        get => _questRegions;
        private set => Set(ref _questRegions, value);
    }

    public IReadOnlyList<QuestMapAssociationViewModel> QuestAssociations
    {
        get => _questAssociations;
        private set => Set(ref _questAssociations, value);
    }

    /// <summary>
    /// Where the player last was: at most one marker, and none when nothing can be placed.
    /// </summary>
    /// <remarks>
    /// Held as a list rather than a nullable so the layer is bound like every other overlay
    /// and disappears on its own when there is no evidence, with no converter and no parent
    /// lookup in the view.
    /// </remarks>
    public IReadOnlyList<PlayerMarkerViewModel> PlayerMarkers
    {
        get => _playerMarkers;
        private set
        {
            Set(ref _playerMarkers, value);
            OnPropertyChanged(nameof(HasPlayerMarker));
        }
    }

    public bool HasPlayerMarker => PlayerMarkers.Count > 0;

    /// <summary>
    /// Where the player has been this raid, projected onto the map.
    /// </summary>
    /// <remarks>
    /// Drawn as a broken line rather than a solid one, because the points are screenshots
    /// minutes apart and the line between two of them is an assumption about a route, not a
    /// route. A dotted line reads as "these places, in this order", which is all that is known.
    /// </remarks>
    public AvaloniaList<Point> PlayerTrail
    {
        get => _playerTrail;
        private set
        {
            Set(ref _playerTrail, value);
            OnPropertyChanged(nameof(HasPlayerTrail));
        }
    }

    /// <summary>A trail needs two points before it is a trail.</summary>
    public bool HasPlayerTrail => PlayerTrail.Count > 1;

    /// <summary>
    /// The trail's stroke in canvas units, so it is two and a half pixels at any zoom.
    /// </summary>
    /// <remarks>
    /// The trail is drawn inside the scaled canvas like everything else, so a fixed thickness
    /// vanished when the map was fitted and became a rope when a building was read up close.
    /// The dash pattern is measured in stroke widths and so follows on its own.
    /// </remarks>
    public double PlayerTrailThickness => 2.5 / Math.Max(ZoomScale, 0.01);

    /// <summary>
    /// Whether the view moves to the player when a new screenshot arrives.
    /// </summary>
    /// <remarks>
    /// On by default, because the whole point of this panel is to be looked at without being
    /// operated. It switches itself off the moment the player pans or zooms by hand, on the
    /// principle that a deliberate action should not be undone by the next screenshot.
    /// </remarks>
    public bool FollowsPlayer
    {
        get => _followsPlayer;
        private set => Set(ref _followsPlayer, value);
    }

    /// <summary>Asks the view to put the player in the middle of the panel.</summary>
    public event EventHandler? PlayerFollowRequested;

    public void ToggleFollowPlayer() => FollowsPlayer = !FollowsPlayer;

    public MapLocation? SelectedLocation
    {
        get => _selectedLocation;
        private set => Set(ref _selectedLocation, value);
    }

    public MapVariant? SelectedVariant
    {
        get => _selectedVariant;
        private set => Set(ref _selectedVariant, value);
    }

    public MapFloorDefinition? SelectedFloor
    {
        get => _selectedFloor;
        private set
        {
            Set(ref _selectedFloor, value);
            // The building's name belongs to the floor being shown, so changing floor by hand
            // changes it too rather than leaving the last one's label behind.
            UpdateArea();
        }
    }

    /// <summary>
    /// The decoded map artwork.
    /// </summary>
    /// <remarks>
    /// This has to be a decoded image rather than a path. Image.Source is an IImage, the
    /// project compiles bindings, and there is no converter, so binding a file path here
    /// silently rendered nothing at all: every downloaded tile and every rasterized SVG
    /// reached the cache on disk and none of them ever reached the screen.
    /// </remarks>
    public Bitmap? BackgroundImage
    {
        get => _backgroundImage;
        private set
        {
            var replaced = _backgroundImage;
            Set(ref _backgroundImage, value);
            AdoptAspectRatio(value);
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(ShowsFlatBackground));
            OnPropertyChanged(nameof(ShowsPlaceholder));
            if (!ReferenceEquals(replaced, value) && replaced is not null)
            {
                ReleaseLater([replaced]);
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string QuestLayerStatus
    {
        get => _questLayerStatus;
        private set => Set(ref _questLayerStatus, value);
    }

    public double CanvasWidth
    {
        get => _canvasWidth;
        private set
        {
            if (Set(ref _canvasWidth, value))
            {
                OnPropertyChanged(nameof(ViewportWidth));
                RequestFit();
            }
        }
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set
        {
            if (Set(ref _canvasHeight, value))
            {
                OnPropertyChanged(nameof(ViewportHeight));
                RequestFit();
            }
        }
    }

    public double ZoomScale
    {
        get => _zoomScale;
        private set
        {
            Set(ref _zoomScale, value);
            _markerScale.Follow(value);
            // The names hold their size while the discs move together underneath them, so who
            // collides with whom changes on every wheel notch. Place names first, because the
            // marker names are arranged around whichever of them survived.
            ChoosePlaceNames();
            ArrangeNames();
            OnPropertyChanged(nameof(ViewportWidth));
            OnPropertyChanged(nameof(ViewportHeight));
            OnPropertyChanged(nameof(PlayerTrailThickness));
        }
    }

    public double ViewportWidth => CanvasWidth * ZoomScale;

    public double ViewportHeight => CanvasHeight * ZoomScale;

    public string AttributionText => _renderModel?.AttributionText ?? "Map artwork is not loaded.";

    public Uri AttributionUri => _renderModel?.Variant.AuthorLink ?? new Uri("https://tarkov.dev");

    public Uri LicenseUri => MapPresentationService.LicenseUri;

    public string TransformStatus => _renderModel?.TransformMessage ?? "No map transform, so positions cannot be placed";

    public bool HasTiles => Tiles.Count > 0;

    public bool HasBackgroundImage => BackgroundImage is not null;

    public bool ShowsPlaceholder => !HasTiles && !HasBackgroundImage;

    public static MapViewModel CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovCompanion",
            "Maps");
        var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var catalogClient = new TarkovDevMapCatalogClient(
            httpClient,
            TarkovDevMapCatalogClientOptions.CreateDefault(Path.Combine(root, "Catalog")));
        var assetCache = new TarkovDevMapAssetCache(
            httpClient,
            MapAssetCacheOptions.CreateDefault(Path.Combine(root, "Assets")));
        var preferences = new JsonFileMapVariantPreferenceStore(Path.Combine(root, "map-defaults.json"));
        return new(httpClient, catalogClient, assetCache, new(preferences), null, null, null);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var result = await _catalogClient.GetAsync(_lifetime.Token).ConfigureAwait(true);
            if (result.Catalog is null)
            {
                Status = result.Message ?? "Map catalog unavailable.";
                return;
            }

            _mapCatalogProvenance = result.Catalog.Provenance;

            Locations = result.Catalog.Locations
                .Where(location => location.Variants.Any(variant => variant.HasRuntimeAsset))
                .ToArray();
            var initial = Locations.FirstOrDefault(location =>
                    string.Equals(location.Id, "customs", StringComparison.OrdinalIgnoreCase))
                ?? Locations.FirstOrDefault();
            Status = DescribeCatalog(result, Locations.Count);
            if (initial is not null)
            {
                await SelectLocationAsync(initial).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Map catalog unavailable · {exception.Message}";
        }
    }

    /// <summary>
    /// Describes what the catalog load actually produced.
    /// </summary>
    /// <remarks>
    /// A catalog that parses but yields no usable location previously reported the upstream
    /// success message, leaving the user with a blank canvas and text saying it worked.
    /// </remarks>
    private static string DescribeCatalog(MapCatalogLoadResult result, int usableLocations)
    {
        var skipped = result.Catalog?.SkippedLocations ?? [];
        if (usableLocations == 0)
        {
            return skipped.Count == 0
                ? "No map in the catalog has a usable image"
                : $"The tarkov.dev map catalog loaded but no map has a usable image. Skipped {skipped.Count} location(s): {string.Join("; ", skipped)}";
        }

        var loaded = result.Message ?? "Map catalog loaded.";
        return skipped.Count == 0
            ? loaded
            : $"{loaded} Skipped {skipped.Count} upstream location(s): {string.Join("; ", skipped)}";
    }

    /// <summary>
    /// Switches the map to the location the player is currently in.
    /// </summary>
    /// <remarks>
    /// Raid evidence names a location the same way the tarkov.dev catalog does, so the map
    /// can follow the player into a raid without anyone touching the companion. A location
    /// the catalog does not carry is ignored rather than clearing the current view.
    /// </remarks>
    /// <summary>Shows a failed interaction in the map status rather than crashing.</summary>
    public void ReportInteractionFailure(string detail) =>
        Status = $"That map action failed: {detail}";

    public async Task FollowRaidAsync(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (SelectedLocation is not null &&
            string.Equals(SelectedLocation.Id, mapId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var location = Locations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, mapId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
        {
            return;
        }

        await SelectLocationAsync(location).ConfigureAwait(true);
        Status = $"Following the raid on {location.Name}";
    }

    public async Task SelectLocationAsync(MapLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        SelectedLocation = location;
        Variants = location.Variants.Where(variant => variant.HasRuntimeAsset).ToArray();
        var selected = await _selectionService.SelectAsync(location, _lifetime.Token).ConfigureAwait(true);
        if (selected is not null)
        {
            await LoadVariantAsync(selected, persist: false).ConfigureAwait(true);
        }
    }

    public Task SelectVariantAsync(MapVariant variant) => LoadVariantAsync(variant, persist: true);

    /// <summary>
    /// Changes floor because the player asked, which also stops the map choosing for them.
    /// </summary>
    /// <remarks>
    /// A map that yanks itself to another floor while somebody is reading one is worse than a
    /// map that does nothing, so a deliberate choice wins until they turn following back on.
    /// This mirrors how zooming by hand switches off automatic fitting.
    /// </remarks>
    public Task SelectFloorAsync(MapFloorDefinition floor)
    {
        AutoSelectsFloor = false;
        return SelectFloorAsync(floor, automatic: false);
    }

    private async Task SelectFloorAsync(MapFloorDefinition floor, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (SelectedLocation is not { } location || SelectedVariant is not { } variant ||
            !Floors.Any(candidate => string.Equals(candidate.Id, floor.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _selectionLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _selectionLoad.Token;
        try
        {
            SelectedFloor = floor;
            _renderModel = (_renderModel ?? _presentationService.Create(
                location,
                variant,
                artwork: PrefersDrawing ? MapBackgroundKind.Svg : null)).SelectFloor(floor.Id);

            // A floor with tiles normally wins, because most floors only have tiles. The
            // exception is a floor the SVG branch below would actually accept while the player
            // has asked for the drawing; without that test, choosing the drawing on a map like
            // Customs would load tiles for a floor and put the markers back into the wrong
            // space. The test has to mirror the else-if exactly: gating this on PrefersDrawing
            // alone drops floors that have tiles and no SVG layer straight through to the final
            // else, which sets Background to null and shows an empty map.
            var drawingWinsThisFloor = PrefersDrawing
                && variant.SvgPath is not null
                && (string.Equals(floor.Id, "base", StringComparison.Ordinal) || floor.SvgLayer is not null);
            if (floor.TilePath is not null && !drawingWinsThisFloor)
            {
                Tiles = [];
                BackgroundImage = null;
                _renderModel = _renderModel with
                {
                    Background = new(
                        MapBackgroundKind.TileTemplate,
                        floor.TilePath,
                        null,
                        MapAssetAvailability.Available,
                        $"Loading upstream floor '{floor.Name}'."),
                };
                await LoadTilesAsync(variant with { TilePath = floor.TilePath }, cancellationToken).ConfigureAwait(true);
            }
            else if (variant.SvgPath is not null &&
                (string.Equals(floor.Id, "base", StringComparison.Ordinal) || floor.SvgLayer is not null))
            {
                Tiles = [];
                BackgroundImage = null;
                await LoadSvgFloorAsync(variant, floor, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                Tiles = [];
                BackgroundImage = null;
                _renderModel = _renderModel with { Background = null };
                Status = $"No artwork for '{floor.Name}'";
                UpdateOverlayElements();
                NotifyPresentationProperties();
            }

            await RefreshQuestLayerAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Tiles = [];
            BackgroundImage = null;
            Status = $"Map floor unavailable: {exception.Message}";
            NotifyPresentationProperties();
        }
    }

    public void ToggleOverlay(MapOverlayKind kind, bool isVisible)
    {
        if (_renderModel is null)
        {
            return;
        }

        _renderModel = _renderModel.SetLayerVisibility(kind, isVisible);
        UpdateOverlays();
        if (kind == MapOverlayKind.QuestObjectives)
        {
            _ = RefreshQuestLayerAsync();
        }
    }

    public void HighlightOverlay(MapOverlayKind? kind)
    {
        if (_renderModel is null)
        {
            return;
        }

        _renderModel = _renderModel.HighlightLayer(kind);
        UpdateOverlays();
    }

    /// <summary>
    /// Selects a marker, or puts the selected one away when it is clicked again.
    /// </summary>
    /// <remarks>
    /// One gesture for both, because a second control to dismiss a callout is one more thing
    /// to find on a panel that is glanced at. Clicking bare map also clears it.
    /// </remarks>
    public void SelectMarker(MapOverlayElementViewModel marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _selectedMarker = _selectedMarker is { } selected && selected.IsSameFeatureAs(marker) ? null : marker;
        UpdateSelection();
    }

    public void ClearSelection()
    {
        if (_selectedMarker is null)
        {
            return;
        }

        _selectedMarker = null;
        UpdateSelection();
    }

    /// <summary>
    /// Keeps the selection on the same feature after the markers are rebuilt.
    /// </summary>
    /// <remarks>
    /// The markers are rebuilt whenever a layer, a floor or a scan changes. The selection is
    /// re-pointed at whichever new marker stands for the same feature, and dropped when that
    /// feature is no longer on the map, so a callout never hangs over a marker that has gone.
    /// </remarks>
    private void ReconcileSelection()
    {
        if (_selectedMarker is { } selected)
        {
            _selectedMarker = Markers.FirstOrDefault(marker => marker.IsSameFeatureAs(selected));
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_selectedMarker is not { } marker)
        {
            SelectedMarkers = [];
            return;
        }

        SelectedMarkers =
        [
            new(
                marker.Name,
                marker.IsOffered ? marker.KindName + " · offered this raid" : marker.KindName,
                marker.CenterX,
                marker.CenterY)
            {
                Conditions = marker.Detail,
                Scale = _markerScale,
                CanvasWidth = CanvasWidth,
                CanvasHeight = CanvasHeight,
            },
        ];
    }

    /// <summary>
    /// Raised when the map should be scaled to the panel and centred again.
    /// </summary>
    /// <remarks>
    /// Only the view knows how much room the panel actually has, so the view model asks
    /// rather than computes. A newly loaded map raises this so the player sees the whole
    /// thing at once instead of the empty top-left corner of a tile grid.
    /// </remarks>
    public event EventHandler? FitRequested;

    /// <summary>Whether the view should keep refitting as the panel resizes.</summary>
    public bool IsAutoFit
    {
        get => _isAutoFit;
        private set => Set(ref _isAutoFit, value);
    }

    /// <summary>
    /// Whether the map follows the player up and down as well as across.
    /// </summary>
    /// <remarks>
    /// The function that answers "which floor is this position on" has existed and been
    /// correct since floors were added, and nothing ever called it. So the map knew the
    /// player's height, knew which floor that height belonged to, and drew the wrong one until
    /// somebody changed it by hand.
    /// </remarks>
    public bool AutoSelectsFloor
    {
        get => _autoSelectsFloor;
        private set => Set(ref _autoSelectsFloor, value);
    }

    public void ToggleAutoFloor() => AutoSelectsFloor = !AutoSelectsFloor;

    public void ChangeZoom(double wheelDelta)
    {
        var factor = wheelDelta > 0 ? 1.2 : 1 / 1.2;
        SetZoom(ZoomScale * factor);
    }

    /// <summary>Zooms deliberately, which turns off automatic fitting.</summary>
    public void SetZoom(double scale)
    {
        IsAutoFit = false;
        FollowsPlayer = false;
        ZoomScale = ClampZoom(scale);
    }

    /// <summary>
    /// Zooms because the view is following the player, without cancelling the following.
    /// </summary>
    /// <remarks>
    /// Separate from the deliberate zoom, which switches following off. This one is the
    /// consequence of following rather than an instruction from the player.
    /// </remarks>
    public void SetFollowZoom(double scale)
    {
        IsAutoFit = false;
        ZoomScale = ClampZoom(scale);
    }

    /// <summary>Records that the player moved the map themselves.</summary>
    /// <remarks>
    /// Panning is a deliberate act, and the next screenshot should not undo it. Fit and the
    /// follow control both turn following back on, so this is recoverable with one click.
    /// </remarks>
    public void ReportManualPan()
    {
        IsAutoFit = false;
        FollowsPlayer = false;
    }

    /// <summary>Whether this map publishes both a tile set and a drawing.</summary>
    public bool HasArtworkChoice
    {
        get => _hasArtworkChoice;
        private set => Set(ref _hasArtworkChoice, value);
    }

    /// <summary>Whether the drawing is being shown rather than the tiles.</summary>
    public bool PrefersDrawing
    {
        get => _prefersDrawing;
        private set => Set(ref _prefersDrawing, value);
    }

    /// <summary>
    /// Switches between the tile set and the drawing, and remembers the answer for this map.
    /// </summary>
    /// <remarks>
    /// Reloads the variant, because the two are different artwork rather than two views of the
    /// same thing, and the canvas takes its shape from whichever is loaded.
    /// </remarks>
    public async Task ToggleArtworkAsync()
    {
        if (!HasArtworkChoice || SelectedLocation is not { } location || SelectedVariant is not { } variant)
        {
            return;
        }

        await _selectionService.ChooseArtworkAsync(location.Id, !PrefersDrawing, _lifetime.Token).ConfigureAwait(true);
        await LoadVariantAsync(variant, persist: false).ConfigureAwait(true);
    }

    /// <summary>Asks the view to scale the whole map into the panel and centre it.</summary>
    public void RequestFit()
    {
        IsAutoFit = true;
        FollowsPlayer = false;
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scales the map to the room the panel reports.</summary>
    /// <summary>
    /// The part of the canvas that actually has artwork on it.
    /// </summary>
    /// <remarks>
    /// A tile plan covers the upstream bounds, and upstream bounds routinely extend past the
    /// drawn map: on Customs four of twenty tiles have no image at all. Fitting the whole
    /// plan therefore scaled the map down to make room for blank space and parked it in a
    /// corner. Fitting the tiles that actually loaded is what a player means by "fit".
    /// </remarks>
    public Rect ContentBounds { get; private set; }

    public void ApplyFit(double availableWidth, double availableHeight)
    {
        if (CanvasWidth <= 0 || CanvasHeight <= 0 ||
            !double.IsFinite(availableWidth) || availableWidth <= 0 ||
            !double.IsFinite(availableHeight) || availableHeight <= 0)
        {
            return;
        }

        var content = ContentBounds;
        var width = content.Width > 0 ? content.Width : CanvasWidth;
        var height = content.Height > 0 ? content.Height : CanvasHeight;
        ZoomScale = ClampZoom(Math.Min(availableWidth / width, availableHeight / height));
    }

    // A tile grid can be several times the panel's size, so the lower bound has to allow a
    // genuine fit. The previous floor of 0.5 could not show a whole map at once.
    private static double ClampZoom(double scale) => Math.Clamp(scale, 0.05, 8);

    /// <summary>
    /// Reshapes the canvas to the artwork's own proportions.
    /// </summary>
    /// <remarks>
    /// The canvas was a fixed 900 by 620 and the image was stretched to fill it, so every map
    /// whose real shape differed was visibly squashed or pulled. The overlay mapper normalizes
    /// to the same box, so markers stayed consistent with each other while sitting on a
    /// distorted map. Taking the shape from the decoded image fixes the picture and keeps the
    /// mapping correct, because both still describe one rectangle.
    /// </remarks>
    private void AdoptAspectRatio(Bitmap? image)
    {
        if (image is null)
        {
            // Cleared without going through the loader, so the measurement taken from the
            // last picture has to go with it rather than outliving it.
            _backgroundDrawnPixels = default;
            _backgroundPixelSize = default;
        }

        if (Tiles.Count == 0 && image is { } artwork)
        {
            var size = artwork.PixelSize;
            if (size.Width > 0 && size.Height > 0)
            {
                // Tiled maps keep the canvas the tile plan gave them, which is already true
                // to scale. Everything else takes its shape from the picture.
                const double longestEdge = 1200;
                var scale = longestEdge / Math.Max(size.Width, size.Height);
                CanvasWidth = Math.Round(size.Width * scale);
                CanvasHeight = Math.Round(size.Height * scale);
            }
        }

        UpdateContentBounds();
    }

    /// <summary>
    /// Decides what Fit frames, from whichever of the two pictures actually drew something.
    /// </summary>
    /// <remarks>
    /// Reported on Shoreline's third floor: the building sat as a thumbnail in the middle of
    /// an otherwise empty panel, fitted at 109%, which is the whole map scaled to the window.
    /// A floor layer replaces the tiles' artwork without removing the tiles, so the grid still
    /// reported the extent of the entire map and Fit framed all of it.
    ///
    /// Where both drew, the smaller one wins. That is the floor layer inside the map rather
    /// than the map itself, and framing one building when the player has chosen a floor is the
    /// point of choosing a floor. A background that covers the same ground as the tiles is not
    /// smaller and does not take over.
    ///
    /// Only fitting and centring use this. The world-to-canvas projection still spans the full
    /// canvas, because that is the rectangle the map's own transform describes, and moving it
    /// would put every marker in the wrong place to make the picture bigger.
    /// </remarks>
    private void UpdateContentBounds() =>
        ContentBounds = AreaBoundsOnCanvas() ?? MapFitBounds.Choose(MeasureContent(Tiles), DrawnBounds());

    /// <summary>
    /// The building the player is in, as a rectangle on the canvas, when one is being framed.
    /// </summary>
    /// <remarks>
    /// Reported as wanting the separate smaller maps some buildings have. The catalog does not
    /// publish separate maps and publishes something better: 68 named rectangles across its
    /// floors -- "dorms", "oilrig &amp; panda", "warehouse 17", "m showroom" -- each with its own
    /// heights. Framing one is what a separate map would have been, and it needs no new data.
    ///
    /// Returned only while somebody has asked for it. A floor chosen deliberately still frames
    /// the floor, because a player who picked "3rd Floor" and got one room would have lost the
    /// map without asking to.
    /// </remarks>
    private Rect? AreaBoundsOnCanvas()
    {
        if (!_frameArea || _areaBounds is not { } bounds || CreateCanvasMapper() is not { } mapper ||
            _renderModel is null)
        {
            return null;
        }

        // Both corners through the map's own transform, because the rectangle is in world
        // coordinates and the canvas is not: a rotated map turns a world-aligned rectangle
        // into a rotated one, and the corners are what say where it ended up.
        if (!_renderModel.TryMapPosition(new(bounds.First.X, 0, bounds.First.Y), out var first) ||
            !_renderModel.TryMapPosition(new(bounds.Second.X, 0, bounds.Second.Y), out var second))
        {
            return null;
        }

        var one = mapper(first);
        var two = mapper(second);
        var left = Math.Min(one.X, two.X);
        var top = Math.Min(one.Y, two.Y);
        var width = Math.Abs(one.X - two.X);
        var height = Math.Abs(one.Y - two.Y);
        // A little room around it, because a building framed edge to edge gives no sense of
        // which way out is which.
        var margin = Math.Max(width, height) * 0.15;
        return width > 0 && height > 0
            ? new Rect(left - margin, top - margin, width + (margin * 2), height + (margin * 2))
            : null;
    }

    /// <summary>
    /// Where the drawn map sits inside the canvas, in canvas coordinates.
    /// </summary>
    /// <remarks>
    /// An upstream SVG's own bounds routinely enclose far more than it draws: Streets renders
    /// into roughly two thirds of the box it declares, and the rest is transparent. Fitting
    /// the declared box therefore scaled the map down to make room for empty space and then
    /// centred the view on that space, which is what "the scaling is wrong" looked like on
    /// screen. Fitting what was actually drawn is what a player means by fit.
    ///
    /// Only fitting and centring use this. The world-to-canvas projection still spans the full
    /// canvas, because that is the rectangle the map's own transform describes, and moving it
    /// would put every marker in the wrong place to make the picture bigger.
    /// </remarks>
    private Rect DrawnBounds()
    {
        var drawn = _backgroundDrawnPixels;
        var size = _backgroundPixelSize;
        if (drawn.Width <= 0 || drawn.Height <= 0 || size.Width <= 0 || size.Height <= 0 ||
            CanvasWidth <= 0 || CanvasHeight <= 0)
        {
            return default;
        }

        // The picture is stretched to fill the canvas, so the two scales are read from the
        // canvas rather than assumed equal. On a tiled map the canvas came from the tile plan
        // and the floor layer's own pixels have nothing to do with it.
        var scaleX = CanvasWidth / size.Width;
        var scaleY = CanvasHeight / size.Height;
        return new(
            Math.Round(drawn.X * scaleX),
            Math.Round(drawn.Y * scaleY),
            Math.Round(drawn.Width * scaleX),
            Math.Round(drawn.Height * scaleY));
    }

    /// <summary>
    /// Finds the part of a decoded map image that has anything drawn on it.
    /// </summary>
    /// <remarks>
    /// A pixel counts as drawn when any of its four bytes is set. The artwork is rasterized
    /// onto a surface cleared to transparent and kept premultiplied, so an untouched pixel is
    /// four zero bytes in every format this could be decoded into; that makes the test
    /// independent of channel order, which reading the alpha byte by position would not be.
    ///
    /// Rows and columns holding only a handful of pixels are ignored, so one stray mark in a
    /// corner cannot drag the measured rectangle back out to the full image and undo the
    /// whole point of measuring.
    /// </remarks>
    private static PixelRect MeasureDrawnPixels(Bitmap image)
    {
        var size = image.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return default;
        }

        var stride = size.Width * 4;
        var length = stride * size.Height;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            image.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), buffer, length, stride);
            var pixels = new byte[length];
            Marshal.Copy(buffer, pixels, 0, length);

            var rowCounts = new int[size.Height];
            var columnCounts = new int[size.Width];
            for (var y = 0; y < size.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < size.Width; x++)
                {
                    var at = row + (x * 4);
                    if ((pixels[at] | pixels[at + 1] | pixels[at + 2] | pixels[at + 3]) != 0)
                    {
                        rowCounts[y]++;
                        columnCounts[x]++;
                    }
                }
            }

            var rowFloor = Math.Max(1, size.Width / 200);
            var columnFloor = Math.Max(1, size.Height / 200);
            var top = FirstAbove(rowCounts, rowFloor);
            var bottom = LastAbove(rowCounts, rowFloor);
            var left = FirstAbove(columnCounts, columnFloor);
            var right = LastAbove(columnCounts, columnFloor);
            return top < 0 || left < 0
                ? default
                : new PixelRect(left, top, right - left + 1, bottom - top + 1);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            // A format this cannot read is not worth failing a map load over; the fit simply
            // falls back to the whole canvas, which is what it did before.
            return default;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int FirstAbove(int[] counts, int floor)
    {
        for (var index = 0; index < counts.Length; index++)
        {
            if (counts[index] >= floor)
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastAbove(int[] counts, int floor)
    {
        for (var index = counts.Length - 1; index >= 0; index--)
        {
            if (counts[index] >= floor)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// A tile smaller than this carries no drawn map.
    /// </summary>
    /// <remarks>
    /// Upstream serves a valid PNG for every position in the grid, including the ones outside
    /// the drawn map, so "the tile loaded" says nothing about whether anything is on it. The
    /// empty ones are about a kilobyte against a hundred for a real tile, which separates them
    /// cleanly without decoding pixels. A tile misjudged by this only crops the fit slightly;
    /// it is still drawn.
    /// </remarks>
    private const long BlankTileBytes = 8 * 1024;

    private static bool HasArtwork(string path)
    {
        try
        {
            return new FileInfo(path).Length > BlankTileBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Measures the rectangle the drawn map occupies, in canvas coordinates.</summary>
    private static Rect MeasureContent(IReadOnlyList<MapTileViewModel> tiles)
    {
        // Measuring every loaded tile was the same as measuring the whole grid, which is what
        // made Fit shrink the map to make room for empty space.
        //
        // A grid where no tile carries artwork measures to nothing rather than to the whole
        // plan. That is what a floor layer looks like: the tiles are still listed, the floor's
        // own drawing replaces them, and taking the plan's extent meant Fit framed the entire
        // map when the player had asked for one building inside it.
        IReadOnlyList<MapTileViewModel> measured = tiles.Where(tile => tile.HasArtwork).ToArray();
        if (measured.Count == 0)
        {
            return default;
        }

        var left = measured.Min(tile => tile.Left);
        var top = measured.Min(tile => tile.Top);
        var right = measured.Max(tile => tile.Left + tile.Size);
        var bottom = measured.Max(tile => tile.Top + tile.Size);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _lifetime.Dispose();
        _ownedHttpClient?.Dispose();

        _backgroundImage?.Dispose();
        _backgroundImage = null;
        foreach (var tile in _tiles)
        {
            tile.Image.Dispose();
        }

        _tiles = [];
    }

    /// <summary>Decodes a cached image file off the UI thread.</summary>
    private static async Task<Bitmap?> LoadBitmapAsync(string? path, CancellationToken cancellationToken)
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

    /// <summary>
    /// Loads the map artwork and measures how much of it is actually drawn on.
    /// </summary>
    /// <remarks>
    /// The measurement walks every pixel, so it runs on a worker thread beside the decode
    /// rather than on the interface thread. It happens once per map load, against an image no
    /// larger than a few megabytes, and only for the single background; tiles are already true
    /// to scale and are not measured.
    /// </remarks>
    private async Task<Bitmap?> LoadArtworkAsync(string? path, CancellationToken cancellationToken)
    {
        var image = await LoadBitmapAsync(path, cancellationToken).ConfigureAwait(true);
        _backgroundPixelSize = image?.PixelSize ?? default;
        _backgroundDrawnPixels = image is null
            ? default
            : await Task.Run(() => MeasureDrawnPixels(image), cancellationToken).ConfigureAwait(true);
        return image;
    }

    /// <summary>
    /// Disposes replaced artwork once the current render pass has finished with it.
    /// </summary>
    private static void ReleaseLater(IEnumerable<Bitmap> images)
    {
        var retained = images.ToArray();
        if (retained.Length == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var image in retained)
                {
                    image.Dispose();
                }
            },
            DispatcherPriority.Background);
    }

    private async Task LoadVariantAsync(MapVariant variant, bool persist)
    {
        if (SelectedLocation is not { } location ||
            !string.Equals(location.Id, variant.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Held for the whole load so automatic floor following stays out of the way. Both
        // paths cancel the same token source, and a screenshot landing mid-load would cancel
        // the variant out from under itself. A raid starting is exactly when both happen at
        // once: the map switches and a position arrives moments later.
        _isLoadingVariant = true;
        // A new map means the floor the last one settled on says nothing about this one.
        _flooredPositionFilename = null;
        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _selectionLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _selectionLoad.Token;
        try
        {
            if (persist)
            {
                await _selectionService.ChooseAsync(location, variant.Key, cancellationToken).ConfigureAwait(true);
            }

            SelectedVariant = variant;
            Floors = variant.Floors;
            SelectedFloor = variant.Floors.FirstOrDefault(floor => floor.IsVisibleByDefault) ?? variant.Floors.FirstOrDefault();
            Tiles = [];
            BackgroundImage = null;
            CanvasWidth = 900;
            CanvasHeight = 620;
            ZoomScale = 1;
            Status = $"Loading {location.Name} · {variant.DisplayName}…";

            // Several maps publish both a photographic tile set and a drawing, and which reads
            // better depends on the map rather than on a preference anyone can set once. An
            // aerial photograph suits a city and renders an interior as a flat brown mass, so
            // the choice is offered per map and only where there is actually a choice.
            HasArtworkChoice = variant.TilePath is not null && variant.SvgPath is not null;
            PrefersDrawing = HasArtworkChoice &&
                await _selectionService.PrefersDrawingAsync(location.Id, cancellationToken).ConfigureAwait(true);

            if (variant.TilePath is not null && !PrefersDrawing)
            {
                _renderModel = _presentationService.Create(
                    location,
                    variant,
                    companionElements: await LoadFeaturesAsync(location, variant, cancellationToken).ConfigureAwait(true),
                    artwork: MapBackgroundKind.TileTemplate);
                await LoadTilesAsync(variant, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                var cached = variant.SvgPath is null
                    ? new MapAssetCacheResult(null, "No artwork for this view")
                    : await _assetCache.GetSvgAsync(variant, SelectedFloor, cancellationToken).ConfigureAwait(true);
                var availability = cached.Asset?.Availability ?? MapAssetAvailability.Unavailable;
                // Say which artwork is on screen. Left to be inferred, this branch produced a
                // render model claiming tiles while displaying the drawing, and every marker
                // was then projected through tile pixel space onto it.
                _renderModel = _presentationService.Create(
                    location,
                    variant,
                    cached.Asset?.LocalPath,
                    availability,
                    cached.Message,
                    await LoadFeaturesAsync(location, variant, cancellationToken).ConfigureAwait(true),
                    artwork: MapBackgroundKind.Svg);
                BackgroundImage = await LoadArtworkAsync(cached.Asset?.RenderPath, cancellationToken).ConfigureAwait(true);
                Status = cached.Asset is not null
                    ? $"{cached.Message}"
                    : cached.Message ?? "Map artwork unavailable.";
            }

            UpdateOverlays();
            NotifyPresentationProperties();
            await RefreshQuestLayerAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Map unavailable: {exception.Message}";
        }
        finally
        {
            // In a finally because a cancelled load is the common case, not the exception: one
            // map selection supersedes another every time a raid starts. Leaving this set
            // would switch automatic floor following off silently and permanently.
            _isLoadingVariant = false;
        }
    }

    private async Task LoadSvgFloorAsync(
        MapVariant variant,
        MapFloorDefinition floor,
        CancellationToken cancellationToken)
    {
        var cached = await _assetCache.GetSvgAsync(variant, floor, cancellationToken).ConfigureAwait(true);
        var availability = cached.Asset?.Availability ?? MapAssetAvailability.Unavailable;
        _renderModel = _renderModel is null
            ? null
            : _renderModel with
            {
                Background = variant.SvgPath is null
                    ? null
                    : new(
                        MapBackgroundKind.Svg,
                        variant.SvgPath,
                        cached.Asset?.LocalPath,
                        availability,
                        cached.Message),
                SelectedFloor = floor,
            };
        // The solid floor follows the chooser, so picking a floor in the stacked view does what
        // picking a floor does in the flat one.
        if (_isStacked)
        {
            await LoadFloorStackAsync(cancellationToken).ConfigureAwait(true);
        }
        BackgroundImage = await LoadArtworkAsync(cached.Asset?.RenderPath, cancellationToken).ConfigureAwait(true);
        Status = cached.Asset is not null
            ? $"{cached.Message}"
            : cached.Message ?? $"Floor '{floor.Name}' is unavailable.";
        UpdateOverlayElements();
        NotifyPresentationProperties();
    }

    /// <summary>
    /// Turns the projected objectives into one row per quest.
    /// </summary>
    /// <remarks>
    /// By quest rather than by objective, because a quest with four things to do on one map is
    /// one decision and four rows of the same name is a list nobody reads. Pinned first: that
    /// is the player saying which one they are actually doing.
    ///
    /// A quest whose marks are all approximate says so once, on its own row. The distinction
    /// matters on a map: an exact point is somewhere to walk to and an association is only a
    /// claim that the quest has something to do with this map.
    /// </remarks>
    /// <summary>The grouping, exposed so it can be checked without standing up a map.</summary>
    public static IReadOnlyList<QuestPanelViewModel> SummarizeQuestsForTest(
        IReadOnlyList<QuestMapObjectiveProjection> objectives) => SummarizeQuests(objectives);

    private static IReadOnlyList<QuestPanelViewModel> SummarizeQuests(
        IReadOnlyList<QuestMapObjectiveProjection> objectives) => objectives
        .GroupBy(objective => objective.TaskName, StringComparer.Ordinal)
        .Select(group => new QuestPanelViewModel(
            group.Key,
            string.Join(" · ", group
                .Select(objective => objective.Description)
                .Where(description => !string.IsNullOrWhiteSpace(description))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(3)),
            group.Any(objective => objective.IsPinned),
            group.All(objective => !objective.HasExactGeometry)))
        .OrderByDescending(quest => quest.IsPinned)
        .ThenBy(quest => quest.Task, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public Task RefreshQuestLayerAsync() => RefreshQuestLayerAsync(CancellationToken.None);

    private async Task RefreshQuestLayerAsync(CancellationToken cancellationToken)
    {
        var refreshGeneration = Interlocked.Increment(ref _questRefreshGeneration);
        if (_renderModel?.Overlays.SingleOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives)?.IsVisible != true)
        {
            ClearQuestLayer("Quest layer is off. Enable it to show static active or pinned objectives.");
            return;
        }

        if (_profileService is null || _questReadService is null || _questProjectionService is null)
        {
            ClearQuestLayer("Quest data unavailable");
            return;
        }

        if (SelectedLocation is not { } location ||
            SelectedVariant is not { } variant ||
            _mapCatalogProvenance is null)
        {
            ClearQuestLayer("Pick a map view to place quests");
            return;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var mapIds = QuestMapProjectionService.CompatibleMapIds(location, variant)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var query = await _questReadService
                .GetActiveMapObjectivesAsync(scope, mapIds, cancellationToken)
                .ConfigureAwait(true);
            if (refreshGeneration != Volatile.Read(ref _questRefreshGeneration) ||
                !ReferenceEquals(SelectedLocation, location) ||
                !ReferenceEquals(SelectedVariant, variant))
            {
                return;
            }

            var projection = _questProjectionService.Project(
                query,
                location,
                variant,
                SelectedFloor,
                _mapCatalogProvenance);
            _questProjection = projection;
            QuestAssociations = _questProjection.Objectives.Select(objective => new QuestMapAssociationViewModel(
                $"{objective.TaskName} · {objective.ObjectiveKind}",
                objective.Availability,
                $"{objective.Attribution} · quest catalog {FormatUtc(objective.QuestCatalogProvenance.ValidatedUtc)} · map catalog {FormatUtc(objective.MapCatalogProvenance.RetrievedUtc)}",
                QuestItemRequirementFormatter.DescribeForMap(
                    objective.ItemTargets,
                    objective.FoundInRaidRequired),
                objective.HasExactGeometry,
                objective.IsUnsupported,
                objective.IsFloorFiltered)).ToArray();
            QuestPanel = SummarizeQuests(_questProjection.Objectives);
            UpdateQuestGeometry();
            var exactCount = _questProjection.Objectives.Count(objective => objective.HasExactGeometry);
            var associationCount = _questProjection.Objectives.Count - exactCount;
            QuestLayerStatus = _questProjection.UnavailableReason ?? (_renderModel?.CanRender == true
                ? $"Quests · {exactCount} placed · {associationCount} roughly"
                : $"No artwork · {exactCount} placed quests hidden · {associationCount} roughly");
            if (_questProjection.OrphanedProgress.Count > 0)
            {
                QuestLayerStatus += $" · {_questProjection.OrphanedProgress.Count} orphaned";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (refreshGeneration == Volatile.Read(ref _questRefreshGeneration))
            {
                ClearQuestLayer($"Quest layer unavailable: {exception.Message}");
            }
        }
    }

    private async Task LoadTilesAsync(MapVariant variant, CancellationToken cancellationToken)
    {
        var zoom = MapCanvasCoordinateMapper.ChooseTileZoom(variant);
        var plan = MapTilePlanner.Plan(variant, zoom, MapCanvasCoordinateMapper.MaximumTilesPerView);
        if (!plan.IsValid)
        {
            Tiles = [];
            Status = plan.Error ?? "PNG tile plan unavailable.";
            return;
        }

        // Tell the coordinate mapper which level this is before anything is placed on it.
        // Everything drawn on the canvas is positioned in 2^zoom space, so a mapper still
        // assuming the pyramid's minimum would put every marker in the wrong place.
        if (_renderModel?.Background is { } planned)
        {
            _renderModel = _renderModel with { Background = planned with { TileZoom = zoom } };
        }

        var loaded = new List<MapTileViewModel>();
        var offlineCount = 0;
        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = plan.Tiles.Select(async tile =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _assetCache
                    .GetTileAsync(variant, tile.Zoom, tile.X, tile.Y, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Asset is not null)
                {
                    var decoded = await LoadBitmapAsync(result.Asset.LocalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (decoded is null)
                    {
                        return;
                    }

                    if (result.Asset.Availability == MapAssetAvailability.CachedOffline)
                    {
                        Interlocked.Increment(ref offlineCount);
                    }

                    lock (loaded)
                    {
                        loaded.Add(new(
                            result.Asset.LocalPath,
                            decoded,
                            tile.Left,
                            tile.Top,
                            tile.Size,
                            HasArtwork(result.Asset.LocalPath)));
                    }
                }
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(true);
        Tiles = loaded.OrderBy(tile => tile.Top).ThenBy(tile => tile.Left).ToArray();
        CanvasWidth = plan.Width;
        CanvasHeight = plan.Height;
        var availability = Tiles.Count == 0
            ? MapAssetAvailability.Unavailable
            : offlineCount > 0
                ? MapAssetAvailability.CachedOffline
                : MapAssetAvailability.Available;
        Status = availability switch
        {
            MapAssetAvailability.CachedOffline => $"Offline · {Tiles.Count} cached tiles at zoom {zoom}",
            MapAssetAvailability.Available when Tiles.Count == plan.Tiles.Count => $"{Tiles.Count} cached tiles at zoom {zoom}",
            MapAssetAvailability.Available => $"{Tiles.Count} of {plan.Tiles.Count} tiles · the rest are blank",
            _ => "No tiles available",
        };
        if (_renderModel?.Background is { } background)
        {
            _renderModel = _renderModel with
            {
                Background = background with { Availability = availability, Message = Status },
            };
        }

        UpdateOverlayElements();
        NotifyPresentationProperties();
    }

    private void UpdateOverlays()
    {
        var elements = _renderModel?.OverlayElements ?? [];
        Overlays = _renderModel?.Overlays
            .Select(layer => new MapOverlayViewModel(
                layer.Kind,
                layer.Name,
                layer.IsVisible,
                layer.IsHighlighted,
                // Counted after the side filter, so the row says how many are on the map
                // rather than how many the catalog holds. A scav told there are twelve
                // extracts and shown seven has been told one of them wrong.
                elements.Count(element => element.Layer == layer.Kind && CanBeTaken(element))))
            .Where(layer => layer.IsListed)
            .OrderBy(layer => layer.Rank)
            .ToArray() ?? [];
        UpdateOverlayElements();
    }

    private void UpdateOverlayElements()
    {
        var mapper = CreateCanvasMapper();
        if (mapper is null || _renderModel is null)
        {
            PlaceNames = [];
            Markers = [];
            ReconcileSelection();
            UpdateQuestGeometry();
            UpdatePlayerMarker();
            return;
        }

        // Highlighting a layer is a request to see it, and the clearest way to show one layer
        // is to fade the rest, so every element knows whether some other layer has the floor.
        var layers = _renderModel.Overlays;
        var anyHighlighted = layers.Any(layer => layer.IsHighlighted);
        var placeNames = new List<MapPlaceNameViewModel>();
        var markers = new List<MapOverlayElementViewModel>();
        foreach (var element in _renderModel.VisibleOverlayElements)
        {
            if (!CanBeTaken(element))
            {
                continue;
            }

            var layer = layers.Single(item => item.Kind == element.Layer);
            var canvasPoint = mapper(element.Position);
            if (!double.IsFinite(canvasPoint.X) || !double.IsFinite(canvasPoint.Y))
            {
                continue;
            }

            var isDimmed = anyHighlighted && !layer.IsHighlighted;
            if (element.Layer == MapOverlayKind.Labels)
            {
                placeNames.Add(new(
                    element.Label,
                    canvasPoint.X,
                    canvasPoint.Y,
                    element.RotationDegrees,
                    PlaceNameFontSize(element.SizePercent),
                    layer.IsHighlighted,
                    isDimmed)
                {
                    Scale = _markerScale,
                    // So a name at the edge of the map can be kept on it. The canvas clips its
                    // children, and a label the catalog put near the edge otherwise loses
                    // whatever falls outside.
                    CanvasWidth = CanvasWidth,
                });
                continue;
            }

            // An extract the player was actually offered stays bright whatever is highlighted,
            // because that is the one they are looking for.
            var isOffered = element.Layer == MapOverlayKind.Extracts && IsOffered(element.Label);
            markers.Add(new(
                element.Label,
                canvasPoint.X,
                canvasPoint.Y,
                KindOf(element),
                layer.IsHighlighted,
                isDimmed && !isOffered,
                isOffered)
            {
                Scale = _markerScale,
                Faction = element.Faction,
                Detail = element.Detail,
                Placement = new(),
            });
        }

        // Two passes, answering different questions. This one drops a place name a marker
        // already carries: "Warehouse 17" as a catalog label and "Warehouse 17" as the exit a
        // few pixels below it. The marker wins, because it says the name and says more — that
        // it is somewhere you can leave from.
        //
        // The result is kept whole rather than trimmed here, because the second pass depends on
        // zoom and has to be redone on every wheel notch; a name dropped at one zoom has to be
        // able to come back at another.
        _allPlaceNames = DropNamesTheMarkersAlreadyCarry(placeNames, markers);
        Markers = markers;
        ChoosePlaceNames();
        ArrangeNames();
        ReconcileSelection();
        UpdateQuestGeometry();
        UpdatePlayerMarker();
        UpdateGroupMarkers();
        // Whether somebody counts as elsewhere depends on which map is open, so the panel is
        // rewritten when the map changes and not only when the group does.
        UpdateGroupPanel(_groupMembers);
    }

    /// <summary>
    /// Finds every marker name a slot that covers nothing, and hides the ones that cannot.
    /// </summary>
    /// <remarks>
    /// Run whenever the markers change and whenever the zoom does. Names hold their size on
    /// screen while the discs move together and apart underneath them, so the arrangement is a
    /// function of zoom and is worthless the moment it changes.
    ///
    /// Spawns and locked doors are left out. Their names only appear under the pointer, and a
    /// hovered name is one name rather than fifty, so reserving room for all of them would
    /// empty the map of the names that are actually drawn.
    /// </remarks>
    /// <summary>How close two of the same name have to be to count as one thing named twice.</summary>
    /// <remarks>
    /// In canvas units before zoom. A place name and an exit describing the same building sit
    /// within a few units of each other; two genuinely different things sharing a name — and
    /// Tarkov has several "Warehouse 4"s — are far enough apart that both survive.
    /// </remarks>
    private const double SameThingWithin = 40;

    private static IReadOnlyList<MapPlaceNameViewModel> DropNamesTheMarkersAlreadyCarry(
        IReadOnlyList<MapPlaceNameViewModel> placeNames,
        IReadOnlyList<MapOverlayElementViewModel> markers)
    {
        if (placeNames.Count == 0 || markers.Count == 0)
        {
            return placeNames;
        }

        var kept = new List<MapPlaceNameViewModel>(placeNames.Count);
        foreach (var name in placeNames)
        {
            var duplicated = markers.Any(marker =>
                Names(marker.Label, name.Text) &&
                Math.Abs(marker.CenterX - name.CenterX) < SameThingWithin &&
                Math.Abs(marker.CenterY - name.CenterY) < SameThingWithin);
            if (!duplicated)
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    /// <summary>
    /// Drops the place names that would be drawn over another one, or under a disc.
    /// </summary>
    /// <remarks>
    /// A place name cannot be moved out of the way: the catalog decides where it goes, and a
    /// name somewhere else is a name for somewhere else. So the only move is to drop one, which
    /// is what this map already does with a marker's name that fits nowhere, and for the same
    /// reason: an unreadable name is worse than a missing one, because it still costs the space
    /// and still has to be read to be dismissed.
    /// </remarks>
    private IReadOnlyList<MapPlaceNameViewModel> _allPlaceNames = [];

    /// <summary>
    /// Draws the place names that fit, and drops the ones that would be drawn over something.
    /// </summary>
    /// <remarks>
    /// Reported with a screenshot of Customs: "Administration Gate" and "Factory Checkpoint"
    /// drawn on top of each other, both clipped and neither readable, and "Warehouse 17" with a
    /// marker's disc sitting on the middle of the word.
    ///
    /// A place name cannot be moved out of the way — the catalog decides where it goes, and a
    /// name somewhere else is a name for somewhere else — so the only move is to drop one. That
    /// is already what this map does with a marker name that fits nowhere, and for the same
    /// reason: an unreadable name is worse than a missing one, because it still costs the space
    /// and still has to be read before it can be dismissed.
    /// </remarks>
    private void ChoosePlaceNames()
    {
        var placeNames = _allPlaceNames;
        var markers = Markers;
        if (placeNames.Count == 0)
        {
            PlaceNames = [];
            return;
        }

        var candidates = placeNames
            .Select(name => new MapPlaceNameCandidate(
                name.CenterX,
                name.CenterY,
                name.TextWidth,
                name.TextHeight,
                name.FontSize,
                name.Text))
            .ToArray();
        var discs = markers.Select(marker => (marker.CenterX, marker.CenterY)).ToArray();
        var drawn = MapPlaceNameLayout.Choose(candidates, discs, ZoomScale);
        // Rebuilt at the current zoom rather than filtered. Two records with equal values are
        // equal, so handing back the same instances would let the items control reuse its
        // containers and never re-read anything computed from the zoom.
        var kept = new List<MapPlaceNameViewModel>(placeNames.Count);
        for (var index = 0; index < placeNames.Count; index++)
        {
            if (drawn[index])
            {
                kept.Add(placeNames[index] with { Zoom = ZoomScale });
            }
        }

        PlaceNames = kept;
    }

    /// <summary>
    /// Whether two labels name the same place.
    /// </summary>
    /// <remarks>
    /// A marker's label carries the faction in brackets where the catalog's place name does
    /// not, so the bracket comes off before they are compared. Loose at both ends because one
    /// of them was typed by a mapper and the other by the game.
    /// </remarks>
    private static bool Names(string markerLabel, string placeName)
    {
        var marker = markerLabel.Split('(')[0].Trim();
        return marker.Length > 0 &&
            string.Equals(marker, placeName.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private void ArrangeNames()
    {
        var markers = Markers;
        var arranged = markers.Where(marker => !marker.IsNameQuiet).ToArray();
        if (arranged.Length == 0)
        {
            return;
        }

        var candidates = arranged
            .Select(marker => new MapLabelCandidate(
                marker.CenterX,
                marker.CenterY,
                marker.EstimatedNameWidth,
                MapOverlayElementViewModel.NameHeight,
                // An exit the player was offered this raid is the one they are looking for, so
                // it keeps its name when something has to lose one. A transit ranks with an
                // exit; a spawn never reaches here.
                marker.IsOffered ? 2 : marker.IsExtract || marker.IsTransit ? 1 : 0))
            .ToArray();

        // The catalog's place names cannot be moved — the catalog decides where, how big and at
        // what angle — so they are handed in as ground that is already taken. Without this a
        // marker's name is placed into a slot a place name is already sitting in, which is half
        // of what was reported.
        var occupied = PlaceNames
            .Select(name => new MapLabelLayout.MapLabelObstacle(
                name.TextLeft * ZoomScale,
                name.TextTop * ZoomScale,
                (name.TextLeft + name.TextWidth) * ZoomScale,
                (name.TextTop + name.TextHeight) * ZoomScale))
            .ToArray();
        var slots = MapLabelLayout.Arrange(candidates, ZoomScale, occupied);
        for (var index = 0; index < arranged.Length; index++)
        {
            var placement = arranged[index].Placement;
            var slot = slots[index];
            if (slot == MapLabelLayout.Hidden)
            {
                placement.IsVisible = false;
                continue;
            }

            placement.IsVisible = true;
            placement.Inset = new(
                0,
                (MapMarkerLayout.Height / 2) + MapLabelLayout.TopOffsetFor(slot, MapOverlayElementViewModel.NameHeight),
                0,
                0);
        }
    }

    /// <summary>
    /// A place name's size on screen, from the catalog's percentage.
    /// </summary>
    /// <remarks>
    /// Names now hold their size on screen rather than scaling with the map, so the range is
    /// tighter than it was: the smallest has to stay readable from a second monitor and the
    /// largest, a region name, must not cover a district when the map is fitted.
    /// </remarks>
    private static double PlaceNameFontSize(double sizePercent) =>
        Math.Clamp(12 * sizePercent / 100, 10, 20);

    /// <summary>
    /// Tells a transit from an extract on the same layer.
    /// </summary>
    /// <remarks>
    /// The projection files both under the extracts layer and marks a transit only by the
    /// arrow it appends to the label, so the arrow is the one signal there is to read.
    /// </remarks>
    private static MapMarkerKind KindOf(MapOverlayElement element) => element.Layer switch
    {
        MapOverlayKind.Keys => MapMarkerKind.Lock,
        MapOverlayKind.Spawns => MapMarkerKind.Spawn,
        _ => element.Label.EndsWith('→') ? MapMarkerKind.Transit : MapMarkerKind.Extract,
    };

    /// <summary>
    /// Takes the position from the player's latest screenshot, or clears it.
    /// </summary>
    /// <remarks>
    /// Called on every runtime snapshot, so it has to be cheap and idempotent. The marker is
    /// only redrawn when the screenshot itself changes; the age in its label is refreshed by
    /// the same snapshot tick that refreshes every other age on screen.
    /// </remarks>
    /// <summary>
    /// Marks the extracts the player has been offered this raid, from their own scan.
    /// </summary>
    /// <remarks>
    /// A map's ten extracts are a reference; the handful a raid actually offers is an answer.
    /// Nothing here guesses which are open, because the game writes that nowhere the companion
    /// can read it; this is only ever the extract list the player scanned.
    /// </remarks>
    /// <summary>
    /// Which side this raid is being run as, so the exits you cannot take come off the map.
    /// </summary>
    /// <remarks>
    /// Reported as "if i am a scav, i should only see scav extracts". A PMC exit is not a
    /// worse option for a scav, it is not an option, and drawing it is worse than drawing
    /// nothing: it sends somebody to a door that will not open.
    ///
    /// Only exits, and only when the side is actually known. Spawns keep both sides because a
    /// scav wants to know where the PMCs started, and an exit whose side the feed never stated
    /// stays on the map rather than being guessed away.
    /// </remarks>
    public void ShowSide(string? side)
    {
        var resolved = side?.Trim().ToLowerInvariant() switch
        {
            "pmc" => MapFeatureFaction.Pmc,
            "scav" => MapFeatureFaction.Scav,
            _ => MapFeatureFaction.Unknown,
        };
        if (resolved == _side)
        {
            return;
        }

        _side = resolved;
        // Through the layers, so the extract row's count follows what is actually drawn.
        UpdateOverlays();
        UpdateSpawnPanel();
    }

    /// <summary>
    /// Whether an exit is one this raid could actually use.
    /// </summary>
    /// <remarks>
    /// Shared exits are for everybody and an exit the feed says nothing about is not known to
    /// be unusable, so both stay. Only an exit stated to be for the other side is removed.
    /// </remarks>
    private bool CanBeTaken(MapOverlayElement element) => CanBeTaken(element, _side);

    /// <summary>The rule on its own, so it can be checked without standing up a map.</summary>
    public static bool CanBeTakenForTest(MapOverlayElement element, MapFeatureFaction side) =>
        CanBeTaken(element, side);

    private static bool CanBeTaken(MapOverlayElement element, MapFeatureFaction side) =>
        side == MapFeatureFaction.Unknown ||
        element.Layer != MapOverlayKind.Extracts ||
        element.Faction is MapFeatureFaction.Unknown or MapFeatureFaction.Shared ||
        element.Faction == side;

    public void ShowActiveExtracts(IReadOnlyList<ActiveExtract> extracts)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        if (_activeExtracts.Count == extracts.Count &&
            _activeExtracts.Select(extract => extract.Name).SequenceEqual(extracts.Select(extract => extract.Name), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _activeExtracts = extracts;
        UpdateOverlayElements();
    }

    /// <summary>Everyone in the group who is on this map, drawn where they last were.</summary>
    public IReadOnlyList<GroupMarkerViewModel> GroupMarkers
    {
        get => _groupMarkers;
        private set
        {
            Set(ref _groupMarkers, value);
            OnPropertyChanged(nameof(HasGroupMarkers));
        }
    }

    public bool HasGroupMarkers => GroupMarkers.Count > 0;

    /// <summary>Where the rest of the group has been this raid, one line each.</summary>
    public IReadOnlyList<GroupTrailViewModel> GroupTrails
    {
        get => _groupTrails;
        private set
        {
            Set(ref _groupTrails, value);
            OnPropertyChanged(nameof(HasGroupTrails));
        }
    }

    public bool HasGroupTrails => GroupTrails.Count > 0;

    /// <summary>
    /// Takes the group's latest positions, to be drawn alongside the player's own.
    /// </summary>
    /// <remarks>
    /// Called on every runtime snapshot, so it returns immediately when nothing has moved. The
    /// comparison is on names and positions rather than the list, which is rebuilt every few
    /// seconds by the group service and would never compare equal.
    /// </remarks>
    /// <summary>Everything the group has marked on this map.</summary>
    public IReadOnlyList<GroupMarkViewModel> GroupMarks
    {
        get => _groupMarks;
        private set => Set(ref _groupMarks, value);
    }

    /// <summary>
    /// Takes the group's waypoints and pings, which arrive with every exchange.
    /// </summary>
    /// <remarks>
    /// The server expires pings, so whatever arrives is current and the client needs no timer
    /// of its own. Redrawn unconditionally rather than compared first, because a ping's whole
    /// life is forty-five seconds and a comparison that skipped a redraw would strand one on
    /// the map after the server had forgotten it.
    /// </remarks>
    public void ShowGroupMarks(IReadOnlyList<GroupWaypointView> waypoints, IReadOnlyList<GroupPingView> pings)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        ArgumentNullException.ThrowIfNull(pings);
        _waypoints = waypoints;
        _pings = pings;
        DropConfirmedPending();
        UpdateGroupMarks();
    }

    /// <summary>
    /// A mark this player has just made, drawn before the group has sent it back.
    /// </summary>
    /// <remarks>
    /// Every mark used to be drawn only once the server returned it, so between the gesture and
    /// the next exchange the map showed nothing at all and the gesture was reported as taking a
    /// second to do anything. It did: five seconds, in the worst case.
    ///
    /// Held separately rather than mixed into the group's own marks, so there is still exactly
    /// one code path drawing what the group has. This is the sender's own copy, drawn hollow
    /// until the real one arrives.
    /// </remarks>
    private sealed record PendingMark(
        string MapId,
        WorldPosition Position,
        bool IsPing,
        DateTimeOffset MadeUtc);

    private readonly List<PendingMark> _pending = [];

    /// <summary>
    /// How long a mark waits for the group to confirm it before it is dropped.
    /// </summary>
    /// <remarks>
    /// Longer than the five-second exchange, so an ordinary round trip always confirms first,
    /// and short enough that a mark the server refused does not sit on the map pretending to be
    /// part of the group's plan.
    /// </remarks>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(12);

    /// <summary>How close a returned mark has to be to count as the one that was sent.</summary>
    /// <remarks>
    /// The server hands back the coordinates it was given, so this only has to survive a
    /// round trip through JSON. A metre is generous for that and far tighter than two people
    /// marking the same doorway.
    /// </remarks>
    private const double PendingMatchMetres = 1.0;

    private void DropConfirmedPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _pending.RemoveAll(pending =>
            now - pending.MadeUtc > PendingLifetime ||
            (pending.IsPing
                ? _pings.Any(ping => Matches(pending, ping.MapId, ping.X, ping.Y, ping.Z))
                : _waypoints.Any(waypoint => Matches(pending, waypoint.MapId, waypoint.X, waypoint.Y, waypoint.Z))));
    }

    private static bool Matches(PendingMark pending, string mapId, double x, double y, double z) =>
        string.Equals(pending.MapId, mapId, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(pending.Position.X - x) < PendingMatchMetres &&
        Math.Abs(pending.Position.Y - y) < PendingMatchMetres &&
        Math.Abs(pending.Position.Z - z) < PendingMatchMetres;

    private void UpdateGroupMarks()
    {
        var mapper = CreateCanvasMapper();
        if (_renderModel is null || mapper is null ||
            (_waypoints.Count == 0 && _pings.Count == 0 && _pending.Count == 0))
        {
            GroupMarks = [];
            return;
        }

        var marks = new List<GroupMarkViewModel>();
        var numbered = 0;
        foreach (var waypoint in _waypoints)
        {
            // Marks belong to a map. Projecting one from another map through this transform
            // would put it somewhere plausible and wrong, the same trap as a member's position.
            if (!IsOnThisMap(waypoint.MapId))
            {
                continue;
            }

            numbered++;
            if (!TryPlace(mapper, waypoint.X, waypoint.Y, waypoint.Z, out var point))
            {
                continue;
            }

            var reached = waypoint.Reached is { Length: > 0 };
            marks.Add(new(
                waypoint.Id,
                point.X,
                point.Y,
                waypoint.Label is { Length: > 0 } label ? label : numbered.ToString(CultureInfo.CurrentCulture),
                reached
                    ? $"{waypoint.Label ?? "Waypoint"} · reached by {waypoint.Reached}"
                    : $"{waypoint.Label ?? "Waypoint"} · marked by {waypoint.By}",
                IsPing: false,
                IsReached: reached)
            {
                Scale = _markerScale,
            });
        }

        foreach (var ping in _pings)
        {
            if (!IsOnThisMap(ping.MapId) ||
                !TryPlace(mapper, ping.X, ping.Y, ping.Z, out var point))
            {
                continue;
            }

            marks.Add(new(
                ping.Id,
                point.X,
                point.Y,
                ping.Label ?? string.Empty,
                $"{ping.By} is pointing here",
                IsPing: true,
                IsReached: false)
            {
                Scale = _markerScale,
            });
        }

        // The sender's own marks, drawn hollow until the group sends them back. Last, so a
        // confirmed mark is never hidden underneath the copy that is waiting to be confirmed.
        foreach (var pending in _pending)
        {
            if (!IsOnThisMap(pending.MapId) ||
                !TryPlace(mapper, pending.Position.X, pending.Position.Y, pending.Position.Z, out var point))
            {
                continue;
            }

            marks.Add(new(
                0,
                point.X,
                point.Y,
                string.Empty,
                pending.IsPing ? "Pinging…" : "Marking a waypoint…",
                pending.IsPing,
                IsReached: false)
            {
                Scale = _markerScale,
                IsPending = true,
            });
        }

        GroupMarks = marks;
    }

    private bool TryPlace(Func<MapPoint, Point> mapper, double x, double y, double z, out Point point)
    {
        point = default;
        if (_renderModel is null || !_renderModel.TryMapPosition(new WorldPosition(x, y, z), out var mapPoint))
        {
            return false;
        }

        var projected = mapper(mapPoint);
        if (!double.IsFinite(projected.X) || !double.IsFinite(projected.Y))
        {
            return false;
        }

        point = projected;
        return true;
    }

    /// <summary>The quests with something to do on this map, for the column beside it.</summary>
    public IReadOnlyList<QuestPanelViewModel> QuestPanel
    {
        get => _questPanel;
        private set
        {
            Set(ref _questPanel, value);
            OnPropertyChanged(nameof(HasQuestPanel));
        }
    }

    public bool HasQuestPanel => QuestPanel.Count > 0;

    /// <summary>Everyone else in the group, written out beside the map.</summary>
    public IReadOnlyList<GroupMemberPanelViewModel> GroupPanel
    {
        get => _groupPanel;
        private set
        {
            Set(ref _groupPanel, value);
            OnPropertyChanged(nameof(HasGroupPanel));
        }
    }

    /// <summary>Whether anybody else is there at all, which is what puts the panel on screen.</summary>
    public bool HasGroupPanel => GroupPanel.Count > 0;

    /// <summary>Where the other players started, for the column beside the map.</summary>
    public IReadOnlyList<SpawnPanelViewModel> SpawnPanel
    {
        get => _spawnPanel;
        private set
        {
            Set(ref _spawnPanel, value);
            OnPropertyChanged(nameof(HasSpawnPanel));
        }
    }

    public bool HasSpawnPanel => SpawnPanel.Count > 0;

    /// <summary>
    /// What the list is anchored to, said plainly.
    /// </summary>
    /// <remarks>
    /// Never "your spawn". The anchor is the first screenshot of the raid, and somebody who
    /// ran for a minute before taking one has an anchor a minute from where they started. The
    /// player can judge that if they are told what it is; they cannot if it is called a spawn.
    /// </remarks>
    public string SpawnPanelDetail
    {
        get => _spawnPanelDetail;
        private set => Set(ref _spawnPanelDetail, value);
    }

    /// <summary>
    /// What the map says is worth looking in, near where the player is standing.
    /// </summary>
    /// <remarks>
    /// Never drawn on the map. Woods alone has 815 loot positions and a map wearing all of them
    /// answers nothing; the question they answer is "what is near me", which is a short list.
    /// </remarks>
    public IReadOnlyList<LootPanelViewModel> LootPanel
    {
        get => _lootPanel;
        private set
        {
            Set(ref _lootPanel, value);
            OnPropertyChanged(nameof(HasLootPanel));
        }
    }

    public bool HasLootPanel => _lootPanel.Count > 0;

    private void UpdateLootPanel()
    {
        if (_mapFeatures.Count == 0 || _playerPosition is not { } position)
        {
            LootPanel = [];
            return;
        }

        LootPanel = LootProximity.Near(_mapFeatures, position.Position)
            .Select(loot => new LootPanelViewModel(
                loot.Name,
                $"{SpawnProximity.Describe(loot.Metres)} {loot.Bearing}"))
            .ToArray();
    }

    /// <summary>
    /// Works out where the other players in this raid started, from where this one did.
    /// </summary>
    /// <remarks>
    /// Rebuilt on a new screenshot, a change of side and a change of map, which is every input
    /// it has. It is cheap: a few hundred points filtered and sorted, once per screenshot.
    /// </remarks>
    /// <summary>Where the other players started, drawn on the map with a line to the player.</summary>
    public IReadOnlyList<SpawnThreatViewModel> SpawnThreats
    {
        get => _spawnThreats;
        private set
        {
            Set(ref _spawnThreats, value);
            OnPropertyChanged(nameof(HasSpawnThreats));
        }
    }

    public bool HasSpawnThreats => _spawnThreats.Count > 0;

    /// <summary>
    /// How long a spawn stays worth drawing, measured from the raid's first screenshot.
    /// </summary>
    /// <remarks>
    /// Full strength for three minutes and gone by eight. Where everybody spawned is the most
    /// useful thing on the map at the start of a raid and noise by the middle of it, and a
    /// layer that never turns itself off is a layer somebody turns off once and never back on.
    /// </remarks>
    private static readonly TimeSpan ThreatsFullFor = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ThreatsGoneAfter = TimeSpan.FromMinutes(8);

    private void UpdateSpawnPanel()
    {
        if (_mapFeatures.Count == 0 || _playerTrailPositions.Count == 0)
        {
            SpawnPanel = [];
            SpawnThreats = [];
            SpawnPanelDetail = string.Empty;
            return;
        }

        var anchor = _playerTrailPositions[0];
        var near = SpawnProximity.Near(
            _mapFeatures,
            anchor.Position,
            _playerPosition?.Position,
            _side);
        UpdateSpawnThreats(near, anchor);
        SpawnPanel = near
            .Select(spawn => new SpawnPanelViewModel(
                spawn.Name,
                SpawnProximity.Describe(spawn.MetresFromStart) + " from your start",
                spawn.MetresFromPlayer is { } fromPlayer && spawn.Bearing is { } bearing
                    ? $"{SpawnProximity.Describe(fromPlayer)} {bearing} of you"
                    : string.Empty))
            .ToArray();
        SpawnPanelDetail = near.Count == 0
            ? string.Empty
            : $"Measured from your first screenshot of this raid, {anchor.Timestamp.ToLocalTime():t}.";
    }

    /// <summary>
    /// Draws the nearby spawns and joins each to the player, fading as the raid goes on.
    /// </summary>
    /// <remarks>
    /// The line is the point. A panel answers "how far and which one"; a line answers "which
    /// way" without reading anything, which is the question in the first ninety seconds.
    ///
    /// Dashed, like every other line this map draws from evidence rather than observation: a
    /// spawn is where somebody probably started, not a route anybody walked.
    /// </remarks>
    private void UpdateSpawnThreats(IReadOnlyList<NearbySpawn> near, ScreenshotPosition anchor)
    {
        var mapper = CreateCanvasMapper();
        var elapsed = DateTimeOffset.UtcNow - anchor.Timestamp.ToUniversalTime();
        var strength = elapsed <= ThreatsFullFor
            ? 1
            : elapsed >= ThreatsGoneAfter
                ? 0
                : 1 - ((elapsed - ThreatsFullFor) / (ThreatsGoneAfter - ThreatsFullFor));
        if (near.Count == 0 || _renderModel is null || mapper is null || strength <= 0)
        {
            SpawnThreats = [];
            return;
        }

        Point? player = null;
        if (_playerPosition is { } position && _renderModel.TryMapPosition(position.Position, out var playerPoint))
        {
            var projected = mapper(playerPoint);
            if (double.IsFinite(projected.X) && double.IsFinite(projected.Y))
            {
                player = projected;
            }
        }

        var threats = new List<SpawnThreatViewModel>();
        foreach (var spawn in near)
        {
            if (!_renderModel.TryMapPosition(spawn.Position, out var mapPoint))
            {
                continue;
            }

            var point = mapper(mapPoint);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            var line = new AvaloniaList<Point>();
            if (player is { } end)
            {
                line.Add(point);
                line.Add(end);
            }

            threats.Add(new(spawn.Name, point.X, point.Y, line, strength)
            {
                Scale = _markerScale,
            });
        }

        SpawnThreats = threats;
    }

    public void ShowGroup(IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        // Colours first: both the panel and the markers read them, and both are rebuilt below.
        _groupColors = GroupMemberColors.Assign(members.Select(member => member.Name));
        // Built every time and assigned only when it differs. The markers below are skipped
        // when nobody has moved, but the panel also carries raid state, the age of a position
        // and what somebody is carrying, all of which change while a position does not.
        UpdateGroupPanel(members);
        if (_groupMembers.Count == members.Count &&
            _groupMembers.Zip(members).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
                Nullable.Equals(pair.First.Position?.X, pair.Second.Position?.X) &&
                Nullable.Equals(pair.First.Position?.Z, pair.Second.Position?.Z)))
        {
            return;
        }

        _groupMembers = members;
        UpdateGroupMarkers();
        UpdateGroupMarks();
    }

    private void UpdateGroupPanel(IReadOnlyList<GroupMemberView> members)
    {
        if (members.Count == 0)
        {
            if (GroupPanel.Count > 0)
            {
                GroupPanel = [];
            }

            return;
        }

        var locations = Locations;
        var rows = members
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(member => Describe(member, IsOnThisMap(member.MapId), locations) with
            {
                Rgb = ColorFor(member.Name),
            })
            .ToArray();
        if (!rows.SequenceEqual(GroupPanel))
        {
            GroupPanel = rows;
        }
    }

    /// <summary>One member written out as a row, with no view model state behind it.</summary>
    /// <param name="member">Them, as they last described themselves.</param>
    /// <param name="isHere">
    /// Whether they are on the map being looked at. Decided by the caller, because a second
    /// client in the group calls the same map something else and only the catalog knows which
    /// names are the same place.
    /// </param>
    /// <param name="locations">The map catalog, only so the row can name a map rather than slug it.</param>
    public static GroupMemberPanelViewModel Describe(
        GroupMemberView member,
        bool isHere,
        IReadOnlyList<MapLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(locations);
        var elsewhere = !isHere;
        var age = member.PositionAge;
        return new(
            member.Name,
            DescribeWhere(member, locations),
            member.Position is { } position
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"{position.X:F0}, {position.Z:F0} · {DescribeAge(age)}")
                : "No position shared",
            string.Join(" · ", member.Loadout.Concat(member.Quests)),
            elsewhere,
            age is null || age > PlayerMarkerFreshFor);
    }

    /// <summary>The map they are on and what they are doing, in that order.</summary>
    /// <remarks>
    /// The map is named rather than slugged, because the chooser above the map names it the
    /// same way and two names for one place reads as two places.
    /// </remarks>
    private static string DescribeWhere(GroupMemberView member, IReadOnlyList<MapLocation> locations)
    {
        var state = DescribeState(member.RaidState);
        if (member.MapId is not { Length: > 0 } mapId)
        {
            return state;
        }

        var name = locations.FirstOrDefault(location =>
            string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase))?.Name ?? mapId;
        return member.Side is { Length: > 0 } side
            ? $"{name} · {state} · {side}"
            : $"{name} · {state}";
    }

    private static string DescribeState(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.InRaid => "In raid",
        RaidLifecycleState.LoadingRaid => "Loading",
        RaidLifecycleState.PostRaid => "Out",
        RaidLifecycleState.Menu => "Menu",
        RaidLifecycleState.LauncherOrGameDetected => "Launcher",
        _ => "Unknown",
    };

    private static string DescribeAge(TimeSpan? age) => age is not { } value
        ? "age unknown"
        : value < TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(0, (int)value.TotalSeconds)}s ago")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)value.TotalMinutes}m ago");

    /// <summary>
    /// Whether something the group shared belongs on the map being looked at.
    /// </summary>
    /// <remarks>
    /// Compared against every id this location answers to rather than against the slug alone.
    /// The quest catalog and the map catalog already disagree about which id a map has, and a
    /// second client in the group is a third opinion: a friend on his own companion sends
    /// whatever his copy calls Customs, and a straight comparison silently dropped his pings
    /// and his position while showing ours.
    ///
    /// Still compared, not ignored. Projecting a mark from another map through this map's
    /// transform puts it somewhere plausible and wrong, which is worse than not drawing it.
    /// </remarks>
    private bool IsOnThisMap(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId) || SelectedLocation is not { } location)
        {
            return false;
        }

        return SelectedVariant is { } variant
            ? QuestMapProjectionService.CompatibleMapIds(location, variant).Contains(mapId)
            : string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This member's colour, or the shared one before a group has been read.</summary>
    private string ColorFor(string name) =>
        _groupColors.TryGetValue(name, out var color) ? color : GroupMemberColors.Fallback;

    private void UpdateGroupMarkers()
    {
        var mapper = CreateCanvasMapper();
        if (_renderModel is null || mapper is null || _groupMembers.Count == 0)
        {
            GroupMarkers = [];
            GroupTrails = [];
            return;
        }

        var rotation = _renderModel.Variant.Transform?.RotationDegrees ?? 0;
        var markers = new List<GroupMarkerViewModel>();
        foreach (var member in _groupMembers)
        {
            // Somebody on another map is not on this one. Projecting their position through
            // this map's transform would place them somewhere plausible and wrong.
            if (member.Position is not { } position ||
                !IsOnThisMap(member.MapId) ||
                !_renderModel.TryMapPosition(position, out var mapPoint))
            {
                continue;
            }

            var point = mapper(mapPoint);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            var bearing = member.HeadingDegrees is { } heading
                ? ((heading - rotation) % 360 + 360) % 360
                : 0;
            var age = member.PositionAge ?? TimeSpan.MaxValue;
            markers.Add(new(
                member.Name,
                point.X,
                point.Y,
                bearing,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{member.Name} · from a screenshot {(age < TimeSpan.FromMinutes(1) ? $"{(int)age.TotalSeconds}s" : $"{(int)age.TotalMinutes}m")} ago"),
                age > PlayerMarkerFreshFor)
            {
                Scale = _markerScale,
                Rgb = ColorFor(member.Name),
            });
        }

        GroupMarkers = markers;
        UpdateGroupTrails(mapper);
    }

    /// <summary>
    /// Draws where each member has been, one line each, in their own colour.
    /// </summary>
    /// <remarks>
    /// Built from the same members the markers are, so a trail can never outlive the dot it
    /// belongs to or be drawn for somebody on another map.
    /// </remarks>
    private void UpdateGroupTrails(Func<MapPoint, Point> mapper)
    {
        var trails = new List<GroupTrailViewModel>();
        foreach (var member in _groupMembers)
        {
            if (member.Trail.Count < 2 || !IsOnThisMap(member.MapId))
            {
                continue;
            }

            var points = new AvaloniaList<Point>();
            foreach (var step in member.Trail)
            {
                if (!_renderModel!.TryMapPosition(new WorldPosition(step.X, 0, step.Z), out var mapPoint))
                {
                    continue;
                }

                var projected = mapper(mapPoint);
                if (double.IsFinite(projected.X) && double.IsFinite(projected.Y))
                {
                    points.Add(projected);
                }
            }

            // The member's own marker is the end of the line, so the line runs to it rather
            // than stopping short of it.
            if (member.Position is { } position &&
                _renderModel!.TryMapPosition(position, out var current))
            {
                var end = mapper(current);
                if (double.IsFinite(end.X) && double.IsFinite(end.Y))
                {
                    points.Add(end);
                }
            }

            if (points.Count < 2)
            {
                continue;
            }

            trails.Add(new(member.Name, points, member.Trail.Max(step => step.Age))
            {
                Rgb = ColorFor(member.Name),
            });
        }

        GroupTrails = trails;
    }

    /// <summary>
    /// Moves to the floor the player is standing on.
    /// </summary>
    /// <remarks>
    /// Only when the floor would actually change, and only when the map is not already loading
    /// something. Both matter: floor selection and variant selection share one cancellation
    /// source, so calling this on every screenshot regardless would turn a race that needs a
    /// human clicking during a load into one that happens by itself.
    ///
    /// The dead band is the filename. A position arrives once per screenshot and the same
    /// screenshot is delivered more than once, so acting on the file rather than the position
    /// means a stairwell cannot make the map flap between two floors on repeats of one frame.
    ///
    /// A floor whose extents do not contain the player leaves the map alone rather than
    /// falling back to the default. Falling back would drag somebody out of a building because
    /// the building's own layer stopped matching at its edge.
    /// </remarks>
    private void FollowFloor(ScreenshotPosition? position)
    {
        if (!AutoSelectsFloor || position is null || _isLoadingVariant ||
            SelectedVariant is not { } variant || variant.Floors.Count <= 1 ||
            string.Equals(_flooredPositionFilename, position.Filename, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _flooredPositionFilename = position.Filename;
        var target = _presentationService.SelectFloor(variant, position.Position);
        if (target is null || SelectedFloor is null ||
            string.Equals(target.Id, SelectedFloor.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = SelectFloorAsync(target, automatic: true);
    }

    /// <summary>Names the building the player is in, or says nothing.</summary>
    private void UpdateArea()
    {
        _areaBounds = _playerPosition is { } position
            ? MapAreaName.Locate(SelectedFloor, position.Position)
            : null;
        Area = _areaBounds?.Description ?? string.Empty;
        OnPropertyChanged(nameof(CanFrameArea));
        // A building that is no longer the one being stood in must not stay framed. Somebody
        // who walks out of dorms and is still looking at dorms has been given a map of
        // somewhere they are not.
        if (_frameArea && _areaBounds is null)
        {
            IsFramingArea = false;
        }
    }

    private MapCatalogBounds? _areaBounds;
    private bool _frameArea;
    private string _area = string.Empty;

    /// <summary>
    /// Which building's floor the player is standing on, where the catalog names one.
    /// </summary>
    /// <remarks>
    /// A floor number alone is not an answer on the maps that have this. The second floor of
    /// dorms on Customs is 2.7 to 6.5 metres and the second floor of big red starts at 5.7, so
    /// "2nd Floor" means two different heights depending on which building you are in. The
    /// floor chosen from a player's height has always taken the named rectangles into account
    /// and never said which one it matched.
    /// </remarks>
    public string Area
    {
        get => _area;
        private set
        {
            Set(ref _area, value);
            OnPropertyChanged(nameof(HasArea));
        }
    }

    /// <summary>Whether there is a building to frame, which is what puts the control on screen.</summary>
    public bool CanFrameArea => _areaBounds is not null;

    /// <summary>
    /// Whether the map draws its floors as a stack rather than one at a time.
    /// </summary>
    /// <remarks>
    /// The flat map answers "where", and on a map with floors it cannot answer "which floor",
    /// which is the question that matters inside a building. A stack answers both at once.
    ///
    /// Off until asked for, and it is the same pictures the flat map already draws — offset,
    /// faded and tilted — rather than a second renderer. That is what makes the flat view
    /// always one toggle away and always correct.
    /// </remarks>
    public bool IsStacked
    {
        get => _isStacked;
        set
        {
            if (_isStacked == value)
            {
                return;
            }

            _isStacked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFloorStack));
            OnPropertyChanged(nameof(StackTilt));
            OnPropertyChanged(nameof(ShowsFlatBackground));
            _ = LoadFloorStackAsync(CancellationToken.None);
        }
    }

    /// <summary>Whether a map has more than one floor to stack at all.</summary>
    public bool CanStack => Floors.Count > 1;

    public bool HasFloorStack => _isStacked && _floorLayers.Count > 0;

    /// <summary>The flat picture gives way to the stack, so the two are never drawn together.</summary>
    public bool ShowsFlatBackground => HasBackgroundImage && !HasFloorStack;

    /// <summary>The floors, lowest first, each with where it sits and how solid it is.</summary>
    public IReadOnlyList<FloorLayerViewModel> FloorLayers
    {
        get => _floorLayers;
        private set
        {
            Set(ref _floorLayers, value);
            OnPropertyChanged(nameof(HasFloorStack));
            OnPropertyChanged(nameof(ShowsFlatBackground));
        }
    }

    /// <summary>
    /// The oblique tilt the whole surface takes in the stacked view.
    /// </summary>
    /// <remarks>
    /// A shear and a vertical squash, which is a cabinet projection: parallel lines stay
    /// parallel and a rectangle stays a rectangle sheared, so nothing has to be re-projected
    /// and every marker still lands where the flat map put it. A perspective camera would need
    /// the whole overlay pipeline to know about depth.
    ///
    /// The identity matrix when the stack is off, so the same transform is always applied and
    /// there is no second code path for the flat view to drift away from.
    /// </remarks>
    public Matrix StackTilt => HasFloorStack
        ? new Matrix(1, 0, -0.34, 0.62, 0, 0)
        : Matrix.Identity;

    private IReadOnlyList<FloorPlacement> _placements = [];

    /// <summary>
    /// Loads every floor's artwork so they can be drawn one above another.
    /// </summary>
    /// <remarks>
    /// The flat view loads one floor because it draws one. A stack needs all of them, which is
    /// a handful of cached assets rather than a download: the asset cache already holds each
    /// floor the player has looked at, and the ones they have not are fetched once.
    ///
    /// A floor whose artwork will not load is left out rather than drawn blank. A gap in the
    /// stack is honest; an empty plate at the right height is a floor that looks empty.
    /// </remarks>
    private async Task LoadFloorStackAsync(CancellationToken cancellationToken)
    {
        if (!_isStacked || _renderModel is null || Floors.Count == 0)
        {
            ReleaseLater(_floorLayers.Select(layer => layer.Image));
            _placements = [];
            FloorLayers = [];
            return;
        }

        var variant = _renderModel.Variant;
        _placements = FloorStack.Arrange(Floors, SelectedFloor);
        // Measured from the floor being read rather than from the lowest one. Every marker on
        // this map is already drawn at the canvas's own coordinates, so putting the chosen
        // floor at zero makes them line up with it without moving a single overlay, and a
        // marker that stayed on the lowest plane while its map rose would point at the wrong
        // floor.
        var baseline = FloorStack.OffsetOf(_placements, SelectedFloor);
        var layers = new List<FloorLayerViewModel>(_placements.Count);
        foreach (var placement in _placements)
        {
            try
            {
                var cached = await _assetCache.GetSvgAsync(variant, placement.Floor, cancellationToken)
                    .ConfigureAwait(true);
                if (await LoadBitmapAsync(cached.Asset?.RenderPath, cancellationToken).ConfigureAwait(true)
                    is not { } image)
                {
                    continue;
                }

                layers.Add(new(placement.Floor.Name, image, placement.Offset - baseline, placement.Opacity));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One floor's artwork is not worth the stack. The rest still says which floors
                // there are and which one is being read.
            }
        }

        ReleaseLater(_floorLayers.Select(layer => layer.Image));
        FloorLayers = layers;
        OnPropertyChanged(nameof(StackTilt));
    }

    /// <summary>
    /// Whether the map is framing the building rather than the floor.
    /// </summary>
    /// <remarks>
    /// Off by default and reset when the player leaves the building. A floor chosen
    /// deliberately still frames the floor: somebody who picked "3rd Floor" and got one room
    /// would have lost the map without asking to.
    /// </remarks>
    public bool IsFramingArea
    {
        get => _frameArea;
        set
        {
            if (_frameArea == value)
            {
                return;
            }

            _frameArea = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameAreaText));
            UpdateContentBounds();
            RequestFit();
        }
    }

    /// <summary>What the control says, which is the thing it will do next.</summary>
    public string FrameAreaText => _frameArea
        ? "Show the whole floor"
        : Area is { Length: > 0 } area ? $"Frame {area}" : "Frame this building";

    public bool HasArea => Area.Length > 0;
    /// <summary>
    /// Whether the map is showing a raid that has already been played.
    /// </summary>
    /// <remarks>
    /// While it is, a live screenshot must not take the marker back: somebody stepping through
    /// last night's raid has said what they want the map to show, and a new screenshot arriving
    /// mid-replay would silently be answering a different question.
    /// </remarks>
    public bool IsReplaying { get; private set; }

    /// <summary>
    /// Shows a recorded raid up to one of its screenshots.
    /// </summary>
    /// <remarks>
    /// The same marker and the same dotted trail the live map uses, because a replay is the
    /// same claim about the same evidence: these places, in this order, each from a screenshot
    /// the player took. Drawing it any other way would invent a distinction that is not there.
    /// </remarks>
    public void ShowReplay(IReadOnlyList<ScreenshotPosition> trail, int step)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Count == 0)
        {
            ClearReplay();
            return;
        }

        var index = Math.Clamp(step, 0, trail.Count - 1);
        IsReplaying = true;
        _playerPosition = trail[index];
        _playerTrailPositions = trail.Take(index + 1).ToArray();
        UpdatePlayerMarker();
        UpdateArea();
        CentreOnReplay();
    }

    /// <summary>Hands the map back to the live raid.</summary>
    public void ClearReplay()
    {
        if (!IsReplaying)
        {
            return;
        }

        IsReplaying = false;
        _playerPosition = null;
        _playerTrailPositions = [];
        UpdatePlayerMarker();
        UpdateArea();
    }

    private void CentreOnReplay()
    {
        FollowsPlayer = true;
        PlayerFollowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowPlayer(ScreenshotPosition? position, IReadOnlyList<ScreenshotPosition> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (IsReplaying)
        {
            return;
        }

        _playerPosition = position;
        _playerTrailPositions = trail;
        UpdatePlayerMarker();
        FollowFloor(position);
        UpdateArea();
        UpdateSpawnPanel();
        UpdateLootPanel();

        // Following happens once per screenshot rather than on every snapshot, or the view
        // would fight the player for control of the map several times a second.
        if (position is null || !FollowsPlayer ||
            string.Equals(_followedPositionFilename, position.Filename, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _followedPositionFilename = position.Filename;
        if (HasPlayerMarker)
        {
            PlayerFollowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A screenshot older than this is drawn faded, because the player has moved since.
    /// </summary>
    private static readonly TimeSpan PlayerMarkerFreshFor = TimeSpan.FromMinutes(2);

    private void UpdatePlayerMarker()
    {
        var mapper = CreateCanvasMapper();
        if (_playerPosition is not { } position || _renderModel is null || mapper is null ||
            !_renderModel.TryMapPosition(position.Position, out var mapPoint))
        {
            PlayerMarkers = [];
            PlayerTrail = [];
            return;
        }

        var trail = new AvaloniaList<Point>();
        foreach (var step in _playerTrailPositions)
        {
            if (_renderModel.TryMapPosition(step.Position, out var stepPoint))
            {
                var projected = mapper(stepPoint);
                if (double.IsFinite(projected.X) && double.IsFinite(projected.Y))
                {
                    trail.Add(projected);
                }
            }
        }

        PlayerTrail = trail;

        var canvasPoint = mapper(mapPoint);
        if (!double.IsFinite(canvasPoint.X) || !double.IsFinite(canvasPoint.Y))
        {
            PlayerMarkers = [];
            return;
        }

        // The heading the screenshot records is a bearing in the world, and the map is drawn
        // with the world turned by its own rotation, so the two differ by exactly that
        // rotation. Drawing the raw heading pointed the cone the wrong way by however much the
        // map was turned, which on some maps is a quarter or a half turn.
        var rotation = _renderModel.Variant.Transform?.RotationDegrees ?? 0;
        var bearing = ((position.HeadingDegrees - rotation) % 360 + 360) % 360;
        var age = DateTimeOffset.UtcNow - position.Timestamp.ToUniversalTime();
        var taken = position.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        PlayerMarkers =
        [
            new(
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"You · {taken} · facing {bearing:F0}°"),
                canvasPoint.X,
                canvasPoint.Y,
                bearing,
                age > PlayerMarkerFreshFor)
            {
                Scale = _markerScale,
            },
        ];
    }

    /// <summary>
    /// Loads the map's extracts, spawns and locked doors, and places them.
    /// </summary>
    /// <remarks>
    /// This data has been downloaded and stored since the first sync and nothing ever drew it,
    /// so the map showed tiles and street names while everything a player actually looks for
    /// sat unused in the database beside them. Extracts are the point: deciding where to leave
    /// from is the question this panel exists to answer.
    ///
    /// A failure here costs the markers and not the map. Tiles and position are worth more
    /// than annotations, and losing the map to a bad catalog row would be a poor trade.
    /// </remarks>
    private async Task<IReadOnlyList<MapOverlayElement>> LoadFeaturesAsync(
        MapLocation location,
        MapVariant variant,
        CancellationToken cancellationToken)
    {
        if (_featureCatalog is null)
        {
            return [];
        }

        try
        {
            var features = await _featureCatalog.GetAsync(location.Id, cancellationToken).ConfigureAwait(true);
            // Kept unprojected as well as projected. The panel measures distances in metres on
            // the ground, which the picture's own coordinates cannot answer: a projection is
            // pixels, and two maps at different scales would give the same run of pixels
            // different meanings.
            _mapFeatures = features;
            UpdateSpawnPanel();
            UpdateLootPanel();
            return MapFeatureProjection.Project(variant, features);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _mapFeatures = [];
            UpdateSpawnPanel();
            UpdateLootPanel();
            return [];
        }
    }

    /// <summary>
    /// Whether a marker names an extract the scan reported as available.
    /// </summary>
    /// <remarks>
    /// Matched on the name rather than an id, because the scan reads names off the screen and
    /// has no id to give. Comparison is loose at both ends: the marker carries the faction in
    /// brackets and optical recognition rarely returns a name character for character.
    /// </remarks>
    private bool IsOffered(string label)
    {
        if (_activeExtracts.Count == 0)
        {
            return false;
        }

        var name = label.Split('(')[0].Trim();
        return _activeExtracts.Any(extract =>
            name.Contains(extract.Name, StringComparison.OrdinalIgnoreCase) ||
            extract.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private Func<MapPoint, Point>? CreateCanvasMapper()
    {
        return _renderModel is null
            ? null
            : MapCanvasCoordinateMapper.Create(_renderModel, CanvasWidth, CanvasHeight);
    }

    /// <summary>
    /// Raised when somebody marks a place, so whoever owns the group session can send it.
    /// </summary>
    /// <remarks>
    /// An event rather than a dependency on the group session, because the map should not need
    /// to know that sharing exists in order to draw itself. It knows where the click was; what
    /// happens next belongs to whoever is doing the sharing.
    /// </remarks>
    public event EventHandler<GroupMarkRequest>? GroupMarkRequested;

    /// <summary>
    /// Asks for a place to be marked for the group, from a point on the canvas.
    /// </summary>
    /// <remarks>
    /// Nothing is drawn here. The mark comes back on the next exchange like everybody else's,
    /// so one code path draws every mark and the sender never sees a version of the group's
    /// state that the group does not have.
    /// </remarks>
    public Task MarkForGroupAsync(Point canvasPoint, bool isPing)
    {
        if (SelectedLocation is not { } location)
        {
            Status = "Pick a map before marking a place on it";
            return Task.CompletedTask;
        }

        if (!TryReadWorldPosition(canvasPoint, out var position))
        {
            // Reported as the gesture doing nothing at all, which it did: it failed silently
            // whatever the reason. A map with no transform cannot turn a click into a place,
            // and saying so is the difference between a broken feature and an unusable map.
            Status = "This map has no transform, so a place cannot be marked on it";
            return Task.CompletedTask;
        }

        Status = isPing ? "Pinging…" : "Marking a waypoint…";
        // Drawn now rather than in five seconds' time. The group's own copy replaces it on the
        // next exchange, and if none arrives it fades rather than sitting there as part of a
        // plan the group never received.
        _pending.Add(new(location.Id, position, isPing, DateTimeOffset.UtcNow));
        UpdateGroupMarks();
        GroupMarkRequested?.Invoke(this, new(location.Id, position, isPing));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Says what became of a mark, because the map draws it only once the server sends it back.
    /// </summary>
    /// <remarks>
    /// One code path draws every mark, including your own, so between the gesture and the next
    /// exchange there is nothing on screen at all. Without a word here that gap is
    /// indistinguishable from the gesture not working, which is exactly how it was reported.
    /// </remarks>
    public void ReportMark(bool isPing, bool sent)
    {
        Status = sent
            ? isPing ? "Pinged" : "Waypoint marked"
            : isPing ? "Ping not sent · check sharing on the Group page" : "Waypoint not sent · check sharing on the Group page";
        if (sent)
        {
            return;
        }

        // A refusal takes the drawn copy with it immediately. Leaving it to time out would
        // show a mark for twelve seconds that the group was never told about.
        var removed = _pending.FindLastIndex(pending => pending.IsPing == isPing);
        if (removed >= 0)
        {
            _pending.RemoveAt(removed);
            UpdateGroupMarks();
        }
    }

    /// <summary>
    /// Turns a point somebody clicked on the canvas into a place in the world.
    /// </summary>
    /// <remarks>
    /// Two inversions, and both are exact rather than approximate. The canvas mapper is
    /// reversed by searching for the map point whose projection lands on the click, which is
    /// cheap because the mapping is affine: two probes give the scale and the offset on each
    /// axis. The transform is then inverted properly by TryUnproject.
    ///
    /// The height cannot be recovered, because projecting throws it away: two places one above
    /// the other land on the same point. The player's own height is used when there is one,
    /// which is the sensible reading of somebody marking a spot on the floor they are on.
    /// </remarks>
    public bool TryReadWorldPosition(Point canvasPoint, out WorldPosition position)
    {
        position = default;
        if (_renderModel?.Variant.Transform is not { IsValid: true } transform ||
            CreateCanvasMapper() is not { } mapper)
        {
            return false;
        }

        // Probe the affine mapping rather than assuming which branch built it. A tiled map and
        // a drawn one are projected differently, and a caller that knew which would have to be
        // changed every time that does.
        var origin = mapper(new MapPoint(0, 0));
        var alongX = mapper(new MapPoint(1, 0));
        var alongY = mapper(new MapPoint(0, 1));
        var scaleX = alongX.X - origin.X;
        var scaleY = alongY.Y - origin.Y;
        if (Math.Abs(scaleX) < 1e-9 || Math.Abs(scaleY) < 1e-9)
        {
            return false;
        }

        var mapPoint = new MapPoint(
            (canvasPoint.X - origin.X) / scaleX,
            (canvasPoint.Y - origin.Y) / scaleY);
        var height = _playerPosition?.Position.Y ?? 0;
        return transform.TryUnproject(mapPoint, height, out position);
    }

    private void UpdateQuestGeometry()
    {
        var questLayer = _renderModel?.Overlays
            .SingleOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives);
        var questLayerVisible = questLayer?.IsVisible == true;
        var questLayerHighlighted = questLayer?.IsHighlighted == true;
        var mapper = CreateCanvasMapper();
        if (!questLayerVisible || _renderModel?.CanRender != true || _questProjection is null || mapper is null)
        {
            QuestPoints = [];
            QuestRegions = [];
            return;
        }

        QuestPoints = _questProjection.Objectives
            .Where(objective => objective.HasExactGeometry && objective.GeometryKind == QuestMapGeometryKind.Point)
            .Select(objective =>
            {
                var point = mapper(objective.Points[0]);
                return new QuestMapPointViewModel(
                    $"{objective.TaskName} · {objective.Description}",
                    point.X,
                    point.Y,
                    objective.IsPinned,
                    questLayerHighlighted)
                {
                    Scale = _markerScale,
                };
            })
            .ToArray();
        QuestRegions = _questProjection.Objectives
            .Where(objective => objective.HasExactGeometry && objective.GeometryKind == QuestMapGeometryKind.Region)
            .Select(objective =>
            {
                var points = new AvaloniaList<Point>();
                points.AddRange(objective.Points.Select(mapper));
                return new QuestMapRegionViewModel(
                    $"{objective.TaskName} · {objective.Description}",
                    points,
                    objective.IsPinned,
                    questLayerHighlighted);
            })
            .ToArray();
    }

    private void ClearQuestLayer(string status)
    {
        _questProjection = null;
        QuestPoints = [];
        QuestRegions = [];
        QuestAssociations = [];
        QuestPanel = [];
        QuestLayerStatus = status;
    }

    private static string FormatUtc(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("u");

    private void NotifyPresentationProperties()
    {
        OnPropertyChanged(nameof(AttributionText));
        OnPropertyChanged(nameof(AttributionUri));
        OnPropertyChanged(nameof(LicenseUri));
        OnPropertyChanged(nameof(TransformStatus));
        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    /// <summary>Assigns a backing field and reports whether the value actually changed.</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
