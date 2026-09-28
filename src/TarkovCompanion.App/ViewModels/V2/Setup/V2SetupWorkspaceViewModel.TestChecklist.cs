using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed partial class V2SetupWorkspaceViewModel
{
    private ICommand? _openTestChecklistCommand;

    /// <summary>Whether this run offers the Test checklist tab: the flag, read once as Setup is built.</summary>
    internal static bool OffersTestChecklist => AppFeatureFlags.Current.IsOn(Flag.TestChecklist);

    /// <summary>The hand-test checklist, or null when the flag is off or the shell was built without one.</summary>
    public TestChecklistViewModel? TestChecklist { get; private set; }

    public bool HasTestChecklist => TestChecklist is not null;

    public bool IsTestChecklistSelected => Selected == V2SetupSection.TestChecklist;

    public string TestChecklistHeading => TestChecklistText.Heading;

    public string OpenTestChecklistLabel => TestChecklistText.Open;

    /// <summary>Diagnostics' way into the checklist's own section.</summary>
    public ICommand OpenTestChecklistCommand => _openTestChecklistCommand ??= new DelegateCommand(() => Select(V2SetupSection.TestChecklist));

    /// <summary>Hands this page the checklist, for the reason AttachSelfTest gives.</summary>
    public void AttachTestChecklist(TestChecklistViewModel checklist)
    {
        TestChecklist = checklist ?? throw new ArgumentNullException(nameof(checklist));
        OnPropertyChanged(nameof(TestChecklist));
        OnPropertyChanged(nameof(HasTestChecklist));
    }
}
