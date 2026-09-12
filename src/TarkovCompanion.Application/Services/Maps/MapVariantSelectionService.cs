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
