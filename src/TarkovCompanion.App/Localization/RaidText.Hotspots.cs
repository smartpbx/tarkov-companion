using TarkovCompanion.Application.Services.Strategy.Prior;

namespace TarkovCompanion.App.Localization;

/// <summary>A modelled traffic hotspot's words (#314): the model names the place and the facts behind it.</summary>
public static partial class RaidText
{
    /// <summary>What the traffic layer is built from, said wherever it is shown.</summary>
    public static string TrafficSourceClass => UiText.Get("Raid.Traffic.SourceClass");

    /// <summary>The nearest named place, or "Unnamed area".</summary>
    public static string HotspotName(TrafficHotspot hotspot)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        return hotspot.Name ?? UiText.Get("Raid.Traffic.UnnamedArea");
    }

    /// <summary>"high-value loot, lines from spawns".</summary>
    public static string HotspotDrivers(TrafficHotspot hotspot)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        return string.Join(UiText.Get("Raid.Traffic.DriverSeparator"), hotspot.Drivers.Select(driver => PhraseText.Say(driver)));
    }
}
