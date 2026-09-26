namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed partial class V2SetupWorkspaceViewModel
{
    /// <summary>[#712 0-3] The format-health readiness row.</summary>
    public FormatHealthReadinessViewModel? FormatHealth { get; private set; }

    public bool HasFormatHealth => FormatHealth is not null;

    public void AttachFormatHealth(FormatHealthReadinessViewModel formatHealth)
    {
        FormatHealth = formatHealth ?? throw new ArgumentNullException(nameof(formatHealth));
        OnPropertyChanged(nameof(FormatHealth));
        OnPropertyChanged(nameof(HasFormatHealth));
    }
}
