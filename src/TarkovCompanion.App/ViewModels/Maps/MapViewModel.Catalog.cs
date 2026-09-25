namespace TarkovCompanion.App.ViewModels.Maps;

public sealed partial class MapViewModel
{
    private MapCatalogState _catalogState = MapCatalogState.Loading;

    /// <summary>[#292] Whether the map list loaded, and if not, why not.</summary>
    public MapCatalogState CatalogState
    {
        get => _catalogState;
        private set => Set(ref _catalogState, value);
    }

    /// <summary>Asks for the map list again, after a failed load.</summary>
    /// <remarks>Only once nothing is loaded: a second load over a working list would reset the map on screen.</remarks>
    public Task RetryCatalogAsync() =>
        CatalogState is MapCatalogState.Failed or MapCatalogState.LocalOnly
            ? InitializeAsync()
            : Task.CompletedTask;

    private void CatalogFailed(MapCatalogState state)
    {
        Status = MapCatalogStates.Line(state);
        CatalogState = state;
    }
}
