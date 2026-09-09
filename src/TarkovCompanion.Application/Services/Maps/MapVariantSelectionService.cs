namespace TarkovCompanion.Application.Services.Maps;

public interface IMapVariantPreferenceStore
{
    Task<string?> GetAsync(string locationId, CancellationToken cancellationToken);

    Task SetAsync(string locationId, string variantKey, CancellationToken cancellationToken);
}

public sealed class MapVariantSelectionService(IMapVariantPreferenceStore preferenceStore)
{
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
