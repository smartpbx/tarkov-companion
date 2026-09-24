using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

public sealed partial class RaidCockpitViewModel
{
    /// <summary>
    /// [#314] Whether the Draw switch is offered (<see cref="Flag.DrawMode"/>). Off hides the pencil
    /// and refuses Draw mode; lines already drawn, and a squadmate's, are still shown.
    /// </summary>
    public bool IsDrawAvailable => AppFeatureFlags.Current.IsOn(Flag.DrawMode);
}
