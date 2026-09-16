using System.Globalization;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>The selected language, number culture, and time zone used by one map renderer.</summary>
/// <remarks>
/// A renderer used to read ambient process culture and the development machine's local time
/// zone. Paired devices could consequently describe the same evidence differently. The host now
/// supplies all three presentation choices together; tests and the packaged gallery do the same.
/// </remarks>
public sealed class MapSceneRendererPresentation
{
    public MapSceneRendererPresentation(
        CultureInfo culture,
        TimeZoneInfo timeZone,
        IReadOnlyDictionary<string, string> strings)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        ArgumentNullException.ThrowIfNull(strings);
        Strings = new Dictionary<string, string>(strings, StringComparer.Ordinal);
    }

    public CultureInfo Culture { get; }

    public TimeZoneInfo TimeZone { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    public string Get(string key) => Strings.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"The map renderer has no localized text for '{key}'.");

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    public string Number(int value) => value.ToString("N0", Culture);

    public string Percent(double value) => value.ToString("P0", Culture);

    public string Instant(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, TimeZone);
        var offset = local.ToString("'UTC'zzz", CultureInfo.InvariantCulture);
        return Format("Map.DateTimeWithZone", local.ToString("f", Culture), offset);
    }

    public static MapSceneRendererPresentation English(
        CultureInfo culture,
        TimeZoneInfo timeZone) => new(culture, timeZone, EnglishStrings);

    /// <summary>English is the default V2 map resource; another selected language replaces it.</summary>
    public static IReadOnlyDictionary<string, string> EnglishStrings { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Map.DateTimeWithZone"] = "{0} ({1})",
            ["Map.Empty.Map"] = "No visible map objects for this view.",
            ["Map.Empty.List"] = "No matching details for this view.",
            ["Map.Action.ZoomOut"] = "Zoom map out",
            ["Map.Action.ZoomIn"] = "Zoom map in",
            ["Map.Action.Fit"] = "Fit plan",
            ["Map.Action.ClearSelection"] = "Clear selection",
            ["Map.Action.Previous"] = "Previous page",
            ["Map.Action.Next"] = "Next page",
            ["Map.Action.ClearCluster"] = "Show all details",
            ["Map.Loot.Preset"] = "High-value loot only",
            ["Map.Loot.Heading"] = "High-value loot",
            ["Map.Loot.Filters"] = "Loot filters",
            ["Map.Loot.ValueBasis"] = "Value basis",
            ["Map.Loot.MinimumTier"] = "Minimum tier",
            ["Map.Loot.ProfileRelevance"] = "Profile relevance",
            ["Map.Loot.Category"] = "Category",
            ["Map.Loot.Floor"] = "Loot floor",
            ["Map.Loot.ProfileIncluded"] = "Include profile needs",
            ["Map.Loot.ProfileExcluded"] = "Market value only",
            ["Map.Loot.AllCategories"] = "All categories",
            ["Map.Loot.AllFloors"] = "All floors",
            ["Map.Loot.MoreCategories"] = "More category choices are available than can be shown here.",
            ["Map.Loot.MoreFloors"] = "More floor choices are available than can be shown here.",
            ["Map.Loot.Ready"] = "Known potential spawns matching these filters.",
            ["Map.Loot.LayerOff"] = "The high-value loot layer is off. Turn it on in Layers to show markers and rows.",
            ["Map.Loot.Partial"] = "Some spawn knowledge is incomplete or list-only. Open a row for what is unknown.",
            ["Map.Loot.Unavailable"] = "Loot-spawn data is unavailable. Reconnect to refresh, or keep using the map without this layer.",
            ["Map.Loot.NoMatches"] = "No potential spawns match these filters. Clear a category, floor, tier, or profile filter.",
            ["Map.Loot.Empty"] = "No loot-spawn rows to show.",
            ["Map.Loot.FilterUnavailable"] = "Loot filters are unavailable until the map host can rebuild the canonical layer.",
            ["Map.Loot.PresetConflict"] = "The shared map changed before the loot preset completed. Earlier confirmed layer changes remain applied; review the current layers and try again.",
            ["Map.Loot.Coverage"] = "{0} of {1} records positioned · {2} floor-resolved · {3} map-only",
            ["Map.Loot.Page"] = "Loot page {0} of {1} · {2} matching spawns",
            ["Map.Loot.Candidates"] = "{0} of {1} candidates above threshold · {2}",
            ["Map.Loot.FloorUnknown"] = "Floor unknown",
            ["Map.Loot.ListOnly"] = "List only",
            ["Map.Loot.ValueUnknown"] = "Current value unknown",
            ["Map.Loot.ValuePartial"] = "Known values up to {0} ₽ · {1} · incomplete range",
            ["Map.Loot.ValueSingle"] = "{0} ₽ · {1}",
            ["Map.Loot.ValueRange"] = "{0}–{1} ₽ · {2}",
            ["Map.Loot.ProbabilityKnown"] = "Published spawn probability {0}",
            ["Map.Loot.ProbabilityUnknown"] = "Spawn probability unknown · expected value not calculated",
            ["Map.Loot.RespawnUnknown"] = "Respawn behavior unknown",
            ["Map.Loot.AccessUnknown"] = "No sourced access note",
            ["Map.Loot.MoreItems"] = "{0} more possible items",
            ["Map.Loot.Source"] = "Source {0} · data through {1} · {2} confidence",
            ["Map.Loot.ConfidenceUnknown"] = "unknown",
            ["Map.Loot.RowAutomation"] = "{0}. {1}. {2}. {3}. {4}",
            ["Map.Loot.Basis.FleaGross"] = "Flea gross",
            ["Map.Loot.Basis.FleaNet"] = "Flea net",
            ["Map.Loot.Basis.BestTrader"] = "Best trader",
            ["Map.Loot.Basis.BestNet"] = "Best net",
            ["Map.Loot.Basis.ValuePerSquare"] = "Value per square",
            ["Map.Loot.Basis.ProfileUtility"] = "Profile utility",
            ["Map.Loot.Tier.Unknown"] = "Value unknown",
            ["Map.Loot.Tier.BelowThreshold"] = "Below threshold",
            ["Map.Loot.Tier.ProfileRelevant"] = "Profile relevant",
            ["Map.Loot.Tier.Qualifying"] = "Qualifying+",
            ["Map.Loot.Tier.Moderate"] = "Moderate+",
            ["Map.Loot.Tier.High"] = "High+",
            ["Map.Loot.Tier.Exceptional"] = "Exceptional",
            ["Map.Loot.Precision.ExactPoint"] = "Exact sourced point",
            ["Map.Loot.Precision.BoundedArea"] = "Sourced bounded area",
            ["Map.Loot.Precision.RoomOrRegion"] = "Sourced room or region",
            ["Map.Loot.Precision.MapOnly"] = "Map known · location unknown",
            ["Map.Label.Presentation"] = "Map presentation",
            ["Map.Label.Floor"] = "Floor filter",
            ["Map.Label.Plan"] = "Map plan",
            ["Map.Label.Layers"] = "Layers",
            ["Map.Label.Details"] = "Visible details",
            ["Map.Label.Search"] = "Search map details",
            ["Map.Label.SearchPlaceholder"] = "Name, kind, faction, or evidence",
            ["Map.Mode.Flat"] = "2D plan",
            ["Map.Mode.FloorStack"] = "Floor stack",
            ["Map.Mode.Interior"] = "3D interior",
            ["Map.Mode.Fallback"] = "{0} is active in the shared scene; this renderer is showing the floor-filtered 2D plan.",
            ["Map.Mode.FloorStackUnsupported"] = "Floor-stack view needs floor-specific artwork. Use the floor filter in the 2D plan.",
            ["Map.Mode.InteriorUnsupported"] = "Reviewed 3D rendering is not available in this renderer. Use the 2D plan.",
            ["Map.Mode.Unavailable"] = "{0} is unavailable. {1}",
            ["Map.Floor.Unavailable"] = "That floor is not available in this map.",
            ["Map.Layer.Unavailable"] = "That layer is no longer available.",
            ["Map.Change.Pending"] = "Waiting for the shared map to confirm the previous change.",
            ["Map.Background.Bounds"] = "The reviewed scene bounds are too large to project safely. Spatial references are withheld.",
            ["Map.Background.None"] = "No reviewed 2D artwork is available. Spatial references are shown without a background.",
            ["Map.Background.NotCached"] = "Reviewed artwork is not cached on this device. Spatial references remain available.",
            ["Map.Asset.Label"] = "{0} · map {1} · game {2}",
            ["Map.Dense.Points"] = "{0} points are grouped into {1} markers",
            ["Map.Dense.Geometry"] = "showing {0} of {1} shapes on the plan",
            ["Map.Dense.List"] = "{0} details are available across {1} pages",
            ["Map.Dense.Outside"] = "{0} features outside reviewed bounds are list-only",
            ["Map.Dense.Suffix"] = "Use cluster drill-down, search, pages, or layer filters to reach every detail.",
            ["Map.Cluster.Label"] = "{0} nearby items",
            ["Map.Cluster.Detail"] = "Approximate grouping of sourced points. Open this cluster to inspect every record.",
            ["Map.Cluster.Kind"] = "Point cluster",
            ["Map.Cluster.Truth"] = "Multiple source records",
            ["Map.Cluster.Faction"] = "Mixed or unknown factions",
            ["Map.Cluster.Evidence"] = "Open the cluster details for each source and timestamp.",
            ["Map.Cluster.Filter"] = "Cluster: {0} records",
            ["Map.List.Page"] = "Page {0} of {1} · {2} matching details",
            ["Map.Layer.Show"] = "Show {0}",
            ["Map.Layer.Hide"] = "Hide {0}",
            ["Map.Layer.Visible"] = "Visible",
            ["Map.Layer.Hidden"] = "Hidden",
            ["Map.Kind.Extract"] = "Extract",
            ["Map.Kind.Transit"] = "Transit",
            ["Map.Kind.SpawnArea"] = "Spawn area",
            ["Map.Kind.LootSpawn"] = "Loot spawn",
            ["Map.Kind.LootContainer"] = "Loot container",
            ["Map.Kind.Hazard"] = "Hazard",
            ["Map.Kind.Lock"] = "Locked entry",
            ["Map.Kind.QuestObjective"] = "Quest objective",
            ["Map.Kind.Route"] = "Route",
            ["Map.Kind.Risk"] = "Risk area",
            ["Map.Kind.Traffic"] = "Traffic estimate",
            ["Map.Kind.LastKnownPosition"] = "Local last known position",
            ["Map.Kind.TeammateLastKnown"] = "Team-shared last known position",
            ["Map.Kind.Ping"] = "Ping",
            ["Map.Kind.Waypoint"] = "Waypoint",
            ["Map.Kind.Label"] = "Map label",
            ["Map.Kind.Custom"] = "Map object",
            ["Map.Truth.StaticReference"] = "Reference",
            ["Map.Truth.PotentialSpawn"] = "Potential spawn",
            ["Map.Truth.LocalLastKnown"] = "Local last known",
            ["Map.Truth.TeamSharedLastKnown"] = "Team-shared last known",
            ["Map.Truth.HistoricalEstimate"] = "Historical estimate",
            ["Map.Truth.PersonalPlan"] = "Personal plan",
            ["Map.Truth.UserAuthored"] = "Map note",
            ["Map.Truth.Unknown"] = "Unclassified",
            ["Map.Faction.Pmc"] = "PMC",
            ["Map.Faction.Scav"] = "Scav",
            ["Map.Faction.Shared"] = "PMC and Scav",
            ["Map.Faction.Unknown"] = "Faction unknown",
            ["Map.Offer.Offered"] = "Offered this raid",
            ["Map.Offer.NotOffered"] = "Not offered this raid",
            ["Map.Offer.Unknown"] = "Offer status unknown",
            ["Map.Evidence.Confidence"] = " · {0} confidence",
            ["Map.Evidence"] = "{0} · observed {1}{2}",
            ["Map.Estimate"] = "Historical model {0} · observed from {1} · data through {2} · generated {3} · {4} · {5} · transform {6}",
            ["Map.Marker.Automation"] = "{0}. {1}. {2}. {3}{4}",
            ["Map.Marker.OfferSuffix"] = ". {0}",
        };
}
