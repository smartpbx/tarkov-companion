using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>Where the map list is: on its way, here, held back by Local only, or failed.</summary>
public enum MapCatalogState
{
    Loading,
    Loaded,
    LocalOnly,
    Failed,
}

/// <summary>
/// [#292] Turns a catalog load into one state and the words for it.
/// </summary>
/// <remarks>
/// The Raid page used to print the load's message, which is the exception text: "Map catalog is
/// unavailable: Local only is on, so nothing was sent." That is the log's sentence. The page says
/// what happened in plain words and what to do about it; the message goes to the log.
/// </remarks>
public static class MapCatalogStates
{
    public static MapCatalogState From(MapCatalogLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Catalog is not null
            ? MapCatalogState.Loaded
            : result.Failure == MapCatalogFailure.LocalOnly
                ? MapCatalogState.LocalOnly
                : MapCatalogState.Failed;
    }

    /// <summary>The headline for a state with no map, or empty.</summary>
    public static string Title(MapCatalogState state) => state switch
    {
        MapCatalogState.LocalOnly => RaidText.MapCatalogLocalOnlyTitle,
        MapCatalogState.Failed => RaidText.MapCatalogFailedTitle,
        _ => string.Empty,
    };

    /// <summary>The line under the headline, or empty.</summary>
    public static string Detail(MapCatalogState state) => state switch
    {
        MapCatalogState.LocalOnly => RaidText.MapCatalogLocalOnlyDetail,
        MapCatalogState.Failed => RaidText.MapCatalogFailedDetail,
        _ => string.Empty,
    };

    /// <summary>Title and detail on one line, for a status line.</summary>
    public static string Line(MapCatalogState state) => $"{Title(state)} · {Detail(state)}";
}
