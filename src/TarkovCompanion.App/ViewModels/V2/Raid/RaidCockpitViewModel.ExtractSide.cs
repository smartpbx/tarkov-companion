using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#873] Which side's exits Extract options and the map are showing, said in the card.</summary>
/// <remarks>
/// "Its also hard to tell which are the active ones for you and which arent." With the side known
/// the list and the map hold only the player's own exits, and the card says so; with it unknown
/// both sides are shown, the one-side exits faded on the map and dimmed in the list, and the card
/// says why. The Corrections card beside it sets the side by hand.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    /// <summary>How faded a one-side exit is drawn on the map while the side is unknown.</summary>
    internal const double UnsureExtractOpacity = 0.55;

    private MapFeatureFaction _extractSide = MapFeatureFaction.Unknown;

    /// <summary>"PMC raid · your extracts", or that the side is unknown and both are listed.</summary>
    public string ExtractSideNote => ExtractSideNoteFor(_extractSide);

    public bool IsExtractSideUnknown => _extractSide is not (MapFeatureFaction.Pmc or MapFeatureFaction.Scav);

    internal static string ExtractSideNoteFor(MapFeatureFaction side) => side switch
    {
        MapFeatureFaction.Pmc => RaidText.PmcRaidYourExtracts,
        MapFeatureFaction.Scav => RaidText.ScavRaidYourExtracts,
        _ => RaidText.SideUnknownBothShown,
    };

    private void SetExtractSide(MapFeatureFaction side)
    {
        if (_extractSide == side)
        {
            return;
        }

        _extractSide = side;
        OnPropertyChanged(nameof(ExtractSideNote));
        OnPropertyChanged(nameof(IsExtractSideUnknown));
    }
}
