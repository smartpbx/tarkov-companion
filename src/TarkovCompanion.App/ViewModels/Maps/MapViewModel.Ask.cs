using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

public sealed partial class MapViewModel
{
    /// <summary>[#712 2-5] What an exit on this map needs (switch, key, payment, conditions), for the Ask box.</summary>
    /// <remarks>The same catalog features the extract panel lists; null where the feed states nothing.</remarks>
    internal MapExtractRequirements? ExtractRequirementsFor(string exitName) =>
        _mapFeatures.FirstOrDefault(feature =>
            feature.Kind is MapFeatureKind.Extract or MapFeatureKind.Transit &&
            string.Equals(feature.Name, exitName, StringComparison.OrdinalIgnoreCase))?.ExtractRequirements;
}
