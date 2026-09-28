using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.ViewModels.V2.Team;

public sealed partial class TeamWorkspaceViewModel
{
    /// <summary>[#917] The tablet-review switch, at the Devices surface it changes.</summary>
    public SetupFeatureFlagRowViewModel? TabletReviewCards { get; private set; }

    public bool HasTabletReviewCards => TabletReviewCards is not null;

    public void AttachTabletReviewCards(SetupFeatureFlagRowViewModel row)
    {
        TabletReviewCards = row ?? throw new ArgumentNullException(nameof(row));
        OnPropertyChanged(nameof(TabletReviewCards));
        OnPropertyChanged(nameof(HasTabletReviewCards));
    }
}
