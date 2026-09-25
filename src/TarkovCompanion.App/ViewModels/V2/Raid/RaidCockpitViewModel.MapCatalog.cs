using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

public sealed partial class RaidCockpitViewModel
{
    private MapCatalogNoticeViewModel? _mapCatalogNotice;

    /// <summary>[#292] Shown in place of the map when there is no map list to draw from.</summary>
    public MapCatalogNoticeViewModel MapCatalogNotice => _mapCatalogNotice ??= CreateMapCatalogNotice();

    /// <summary>The player asked for Setup › Data & Privacy, to turn Local only off.</summary>
    public event EventHandler? OpenDataPrivacyRequested;

    private MapCatalogNoticeViewModel CreateMapCatalogNotice()
    {
        var notice = new MapCatalogNoticeViewModel(
            () => _map.RetryCatalogAsync(),
            () => OpenDataPrivacyRequested?.Invoke(this, EventArgs.Empty));
        notice.Apply(_map.CatalogState);
        return notice;
    }

    private void MapCatalogStateChanged() => _mapCatalogNotice?.Apply(_map.CatalogState);
}
