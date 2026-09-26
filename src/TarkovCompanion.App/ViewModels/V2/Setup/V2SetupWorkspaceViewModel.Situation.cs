namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed partial class V2SetupWorkspaceViewModel
{
    /// <summary>[#712 0-2] The situation panel; attached only in developer mode.</summary>
    public SituationDiagnosticsViewModel? Situation { get; private set; }

    public bool HasSituation => Situation is not null;

    /// <summary>Hands this page the situation panel, for the reason AttachSelfTest gives.</summary>
    public void AttachSituation(SituationDiagnosticsViewModel situation)
    {
        Situation = situation ?? throw new ArgumentNullException(nameof(situation));
        OnPropertyChanged(nameof(Situation));
        OnPropertyChanged(nameof(HasSituation));
    }
}
