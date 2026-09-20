using System.Globalization;

namespace TarkovCompanion.Application.Services.Maps;

public interface IMapVariantPreferenceStore
{
    Task<string?> GetAsync(string locationId, CancellationToken cancellationToken);

    Task SetAsync(string locationId, string variantKey, CancellationToken cancellationToken);
}

public sealed class MapVariantSelectionService(IMapVariantPreferenceStore preferenceStore)
{
    /// <summary>
    /// The suffix under which a location's artwork choice is remembered.
    /// </summary>
    /// <remarks>
    /// Stored alongside the variant choice rather than in a file of its own, because it is the
    /// same kind of thing: a per-map preference the player set once and should not be asked
    /// about again. The suffix cannot collide with a location id, which are slugs.
    /// </remarks>
    private const string ArtworkSuffix = "#artwork";

    private const string DrawingChoice = "drawing";

    /// <summary>
    /// Whether the player asked for the drawn map rather than the photographic tiles.
    /// </summary>
    /// <remarks>
    /// Several maps publish both, and which is better depends entirely on the map. Streets is
    /// a city and its tiles read like an aerial photograph, which is exactly right. Factory is
    /// an interior and the same treatment renders as a flat brown mass in which nothing can be
    /// told apart, while the drawing of it is a legible floor plan. So this is the player's
    /// call per map, not a global setting and not ours.
    /// </remarks>
    public async Task<bool> PrefersDrawingAsync(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        var stored = await preferenceStore.GetAsync(locationId + ArtworkSuffix, cancellationToken).ConfigureAwait(false);
        return string.Equals(stored, DrawingChoice, StringComparison.Ordinal);
    }

    public Task ChooseArtworkAsync(string locationId, bool prefersDrawing, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return preferenceStore.SetAsync(
            locationId + ArtworkSuffix,
            prefersDrawing ? DrawingChoice : "tiles",
            cancellationToken);
    }

    /// <summary>
    /// The suffix under which a location's rotation is remembered.
    /// </summary>
    /// <remarks>
    /// Alongside the artwork choice and for the same reason: a per-map preference somebody sets
    /// once. Which way round a map wants to be is a fact about that map and the shape of the
    /// screen, and neither changes between raids.
    /// </remarks>
    private const string RotationSuffix = "#rotation";

    /// <summary>
    /// How far round this map should be turned, in degrees clockwise.
    /// </summary>
    /// <remarks>
    /// Reported as Shoreline being very tall and a 1920×1080 screen being very wide, which is
    /// the whole of it: a map whose long axis runs the wrong way is drawn small enough to fit
    /// its height and then wastes half the panel either side of it. Turning it a quarter turn
    /// puts its long axis along the screen's.
    ///
    /// Quarter turns only. Anything else resamples the artwork and leaves the map at an angle
    /// nobody asked for, and the complaint is about which way round a map is, not about fine
    /// adjustment.
    /// </remarks>
    public async Task<int> RotationAsync(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        var stored = await preferenceStore.GetAsync(locationId + RotationSuffix, cancellationToken).ConfigureAwait(false);
        return Normalize(int.TryParse(stored, CultureInfo.InvariantCulture, out var degrees) ? degrees : 0);
    }

    public Task ChooseRotationAsync(string locationId, int degrees, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return preferenceStore.SetAsync(
            locationId + RotationSuffix,
            Normalize(degrees).ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }

    /// <summary>
    /// The nearest quarter turn, as one of 0, 90, 180 or 270.
    /// </summary>
    /// <remarks>
    /// Applied on the way in as well as on the way out. The file is one a player can open and
    /// edit, and a hand-typed 45 or -90 there should not put the map at an angle no button can
    /// undo.
    /// </remarks>
    public static int Normalize(int degrees)
    {
        var quarters = (int)Math.Round(degrees / 90d, MidpointRounding.AwayFromZero) % 4;
        return (quarters < 0 ? quarters + 4 : quarters) * 90;
    }

    /// <summary>
    /// The key the group-name setting is remembered under.
    /// </summary>
    /// <remarks>
    /// Not a location, deliberately. Whether somebody wants their squadmates' names drawn is a
    /// fact about how they read a map, not about which map. The leading character cannot
    /// collide with a location id, which are slugs, for the same reason the two suffixes above
    /// cannot.
    /// </remarks>
    private const string GroupNamesKey = "#group-names";

    /// <summary>Whether squadmates' names are drawn beside their dots. Off unless asked for.</summary>
    /// <remarks>
    /// Reported after it shipped on: "the change with the username showing over the icon on the
    /// map is not great, i could already tell who was who based on the color."
    ///
    /// Which was said in advance, when the feature was approved — "we see the person's colour
    /// in the group pane anyway" — and built on anyway, with an argument about five-mans. The
    /// argument was not wrong about five-mans; it was wrong about what a default is for. The
    /// colour already answers "which of you is that" for the group sizes people actually play,
    /// and a name over every dot is ink on the thing the map is for.
    ///
    /// So the feature stays and the default flips. Somebody in a five-man who wants the names
    /// has one press, and it stays pressed.
    /// </remarks>
    public async Task<bool> GroupNamesAsync(CancellationToken cancellationToken)
    {
        var stored = await preferenceStore.GetAsync(GroupNamesKey, cancellationToken).ConfigureAwait(false);
        return string.Equals(stored, "on", StringComparison.Ordinal);
    }

    public Task ChooseGroupNamesAsync(bool shown, CancellationToken cancellationToken) =>
        preferenceStore.SetAsync(GroupNamesKey, shown ? "on" : "off", cancellationToken);

    /// <summary>
    /// The key the idle auto-hide setting is remembered under.
    /// </summary>
    /// <remarks>
    /// Not a location, for the same reason the group-names key is not: whether the floating
    /// chrome fades when the pointer leaves the map is a fact about how somebody plays, not
    /// about which map is open.
    /// </remarks>
    private const string HideControlsWhenIdleKey = "#hide-controls-idle";

    /// <summary>
    /// Whether the floating toolbars fade out once the pointer has been off the map a few
    /// seconds. On unless somebody turns it off.
    /// </summary>
    /// <remarks>
    /// Absent means never asked, and the default for that is on: the whole point is a second
    /// monitor where the mouse lives in the game, and that is the common case, not the
    /// exception somebody has to opt into.
    /// </remarks>
    public async Task<bool> HideControlsWhenIdleAsync(CancellationToken cancellationToken)
    {
        var stored = await preferenceStore.GetAsync(HideControlsWhenIdleKey, cancellationToken).ConfigureAwait(false);
        return stored is null || string.Equals(stored, "on", StringComparison.Ordinal);
    }

    public Task ChooseHideControlsWhenIdleAsync(bool hide, CancellationToken cancellationToken) =>
        preferenceStore.SetAsync(HideControlsWhenIdleKey, hide ? "on" : "off", cancellationToken);

    /// <summary>The key the last map on screen is remembered under. Not a location id, like the two above.</summary>
    private const string LastMapKey = "#last-map";

    /// <summary>
    /// The map that was on screen last, or null when none has been yet.
    /// </summary>
    /// <remarks>
    /// The app opened on Customs every time, whatever had been played. Somebody who runs Reserve
    /// all week changed map at every launch. A raid starting moves the map as well, so this is
    /// usually simply the last map played.
    /// </remarks>
    public async Task<string?> LastMapAsync(CancellationToken cancellationToken)
    {
        var stored = await preferenceStore.GetAsync(LastMapKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    public Task RememberLastMapAsync(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return preferenceStore.SetAsync(LastMapKey, locationId, cancellationToken);
    }

    public async Task<MapVariant?> SelectAsync(MapLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var savedKey = await preferenceStore.GetAsync(location.Id, cancellationToken).ConfigureAwait(false);
        var saved = location.Variants.FirstOrDefault(variant =>
            variant.HasRuntimeAsset && string.Equals(variant.Key, savedKey, StringComparison.OrdinalIgnoreCase));
        return saved ?? SelectFallback(location);
    }

    public async Task<MapVariant> ChooseAsync(
        MapLocation location,
        string variantKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKey);
        var variant = location.Variants.FirstOrDefault(candidate =>
            candidate.HasRuntimeAsset && string.Equals(candidate.Key, variantKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The selected map variant is unavailable for this location.", nameof(variantKey));

        await preferenceStore.SetAsync(location.Id, variant.Key, cancellationToken).ConfigureAwait(false);
        return variant;
    }

    public static MapVariant? SelectFallback(MapLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Variants.FirstOrDefault(variant => variant.IsInteractive && variant.HasRuntimeAsset)
            ?? location.Variants.FirstOrDefault(variant => variant.HasRuntimeAsset);
    }
}
