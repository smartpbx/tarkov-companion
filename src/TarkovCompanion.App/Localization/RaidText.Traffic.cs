namespace TarkovCompanion.App.Localization;

/// <summary>Modelled traffic: the phase control, the banner, the legend and the plan card's rows (#314).</summary>
/// <remarks>Every one of these says it is modelled. A translation must keep "not live" meaning not live.</remarks>
public static partial class RaidText
{
    public static string TrafficPhaseAndLegend => UiText.Get("Raid.TrafficPhaseAndLegend");
    public static string Phase => UiText.Get("Raid.Phase");
    public static string TrafficRaidPhase => UiText.Get("Raid.TrafficRaidPhase");
    public static string LowerModelledTraffic => UiText.Get("Raid.LowerModelledTraffic");
    public static string Higher => UiText.Get("Raid.Higher");
    public static string TrafficBands => UiText.Get("Raid.TrafficBands");
    public static string BusiestHighestFirst => UiText.Get("Raid.BusiestHighestFirst");
    public static string BusiestPlaces => UiText.Get("Raid.BusiestPlaces");
    public static string PhaseAuto => UiText.Get("Raid.PhaseAuto");
    public static string PhaseEarly => UiText.Get("Raid.PhaseEarly");
    public static string PhaseMid => UiText.Get("Raid.PhaseMid");
    public static string PhaseLate => UiText.Get("Raid.PhaseLate");
    public static string TrafficPhaseTip => UiText.Get("Raid.TrafficPhaseTip");
    public static string PhaseChosenByYou => UiText.Get("Raid.PhaseChosenByYou");
    public static string PhaseFromRaidClock => UiText.Get("Raid.PhaseFromRaidClock");
    public static string PhasePlanningDefault => UiText.Get("Raid.PhasePlanningDefault");
    public static string TrafficBannerTitle => UiText.Get("Raid.TrafficBannerTitle");
    public static string PriorModelled => UiText.Get("Raid.PriorModelled");
    public static string PriorNone => UiText.Get("Raid.PriorNone");
    public static string PhaseRaid(string phase, string basis) => UiText.Format("Raid.PhaseRaid", phase, basis);
    public static string ModelVersion(string version) => UiText.Format("Raid.ModelVersion", version);
    public static string DataThroughGenerated(string through, string time) => UiText.Format("Raid.DataThroughGenerated", through, time);
    public static string ConfidenceLow(double confidence) => UiText.Format("Raid.ConfidenceLow", confidence);
    public static string LevelHighest => UiText.Get("Raid.LevelHighest");
    public static string LevelHigh => UiText.Get("Raid.LevelHigh");
    public static string LevelRaised => UiText.Get("Raid.LevelRaised");
    public static string NoSpawnAreasLower => UiText.Get("Raid.NoSpawnAreasLower");
    public static string NoWaysOut => UiText.Get("Raid.NoWaysOut");
    public static string LootSpawns(int count) => UiText.Format("Raid.LootSpawns", count);
    public static string NoLootData => UiText.Get("Raid.NoLootData");
    public static string CatalogThrough(string date) => UiText.Format("Raid.CatalogThrough", date);
    public static string CatalogDateUnknown => UiText.Get("Raid.CatalogDateUnknown");
    public static string IncludesOwnRaids(int count) => UiText.Plural("Raid.IncludesOwnRaids", count);
    public static string ModelledTrafficAt(string level, string place) => UiText.Format("Raid.ModelledTrafficAt", level, place);
    public static string WhyDrivers(string drivers, string source) => UiText.Format("Raid.WhyDrivers", drivers, source);
    public static string LayerModelledTraffic => UiText.Get("Raid.LayerModelledTraffic");
    public static string NotValidated => UiText.Get("Raid.NotValidated");
    public static string BandNoTint => UiText.Get("Raid.BandNoTint");
    public static string BandAmber => UiText.Get("Raid.BandAmber");
    public static string BandOrange => UiText.Get("Raid.BandOrange");
    public static string BandRed => UiText.Get("Raid.BandRed");
    public static string BandLow(string percent) => UiText.Format("Raid.BandLow", percent);
    public static string BandRaised(string percent) => UiText.Format("Raid.BandRaised", percent);
    public static string BandHigh(string percent) => UiText.Format("Raid.BandHigh", percent);
    public static string BandHighest(string percent) => UiText.Format("Raid.BandHighest", percent);
    public static string LegendRowName(string rank, string label, string value) => UiText.Format("Raid.LegendRowName", rank, label, value);
}
