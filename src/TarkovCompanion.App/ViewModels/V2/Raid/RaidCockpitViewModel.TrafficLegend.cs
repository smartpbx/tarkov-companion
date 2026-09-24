using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Strategy.Prior;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One line of the modelled-traffic legend, as words: where it comes in order, what, how much.</summary>
/// <param name="Rank">"1", "2" ... in the order the list is read.</param>
/// <param name="Label">The colour's meaning, or the place's name.</param>
/// <param name="Value">The band or the level, in words and a number.</param>
public sealed record TrafficLegendRow(string Rank, string Label, string Value)
{
    public string AccessibleName => RaidText.LegendRowName(Rank, Label, Value);
}

/// <summary>
/// [#266] The heat legend without the colour: the ramp's bands, then the hottest places, in order.
/// </summary>
/// <remarks>
/// The legend was a gradient bar with "Lower" and "Higher" at its ends, so the only way to read
/// which part of the map was busiest was to tell amber from red. Colour must not be the only
/// channel, so the same facts are here as ordered text: the bands low to high with the numbers
/// they start at, and the places ranked highest first with their modelled level.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    public IReadOnlyList<TrafficLegendRow> TrafficLegendBands { get; } = TrafficLegend.Bands();

    public IReadOnlyList<TrafficLegendRow> TrafficLegendPlaces =>
        TrafficLegend.Places(_prior is { HasField: true } prior ? prior.Hotspots : []);

    public bool HasTrafficLegendPlaces => TrafficLegendPlaces.Count > 0;
}

/// <summary>The legend's words, built from the same thresholds the picture and the plan card use.</summary>
internal static class TrafficLegend
{
    /// <summary>At most this many places: the plan card shows two, the model finds up to six.</summary>
    public const int MaximumPlaces = 5;

    /// <summary>The ramp low to high, left to right as the bar draws it.</summary>
    /// <remarks>
    /// Below <see cref="TrafficHeatPicture.Floor"/> nothing is drawn at all, which is itself a
    /// band a player needs to know about: bare map means "low", not "no data".
    /// </remarks>
    public static IReadOnlyList<TrafficLegendRow> Bands() =>
    [
        new("1", RaidText.BandNoTint, RaidText.BandLow(Percent(TrafficHeatPicture.Floor))),
        new("2", RaidText.BandAmber, RaidText.BandRaised(Percent(TrafficHeatPicture.Floor))),
        new("3", RaidText.BandOrange, RaidText.BandHigh(Percent(RaidCockpitViewModel.HighTrafficLevel))),
        new("4", RaidText.BandRed, RaidText.BandHighest(Percent(RaidCockpitViewModel.HighestTrafficLevel))),
    ];

    /// <summary>The hottest places, highest first, ties by name so the order is stable.</summary>
    public static IReadOnlyList<TrafficLegendRow> Places(IEnumerable<TrafficHotspot> hotspots) =>
    [
        .. hotspots
            .Where(hotspot => double.IsFinite(hotspot.Intensity) && !string.IsNullOrWhiteSpace(hotspot.Name))
            .OrderByDescending(hotspot => hotspot.Intensity)
            .ThenBy(hotspot => hotspot.Name, StringComparer.CurrentCulture)
            .Take(MaximumPlaces)
            .Select((hotspot, index) => new TrafficLegendRow(
                (index + 1).ToString(CultureInfo.CurrentCulture),
                hotspot.Name,
                $"{RaidCockpitViewModel.TrafficLevel(hotspot.Intensity)} · {Percent(hotspot.Intensity)}")),
    ];

    /// <summary>Whole percent, rounded down.</summary>
    /// <remarks>
    /// Down, not to nearest: an intensity of 0.649 is "Raised", and "Raised · 65%" beside a band
    /// that says high starts at 65% contradicts itself. Found on Customs' Big Red.
    /// </remarks>
    private static string Percent(double value) =>
        (Math.Floor((Math.Clamp(value, 0, 1) * 100) + 1e-9) / 100).ToString("P0", CultureInfo.CurrentCulture);
}
