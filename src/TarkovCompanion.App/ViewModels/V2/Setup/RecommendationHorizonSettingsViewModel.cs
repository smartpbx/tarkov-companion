using TarkovCompanion.App.Localization;
using System.Windows.Input;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed class RecommendationHorizonChoiceViewModel(
    RecommendationHorizon horizon,
    string label,
    string scope,
    Action choose) : BindableViewModel
{
    private bool _isCurrent;

    public RecommendationHorizon Horizon { get; } = horizon;

    public string Label { get; } = label;

    public string DisplayLabel => IsCurrent ? $"✓ {Label}" : Label;

    public string AutomationId => $"v2-setup-recommendation-{scope}-{Horizon.ToString().ToLowerInvariant()}";

    public ICommand ChooseCommand { get; } = new DelegateCommand(choose ?? throw new ArgumentNullException(nameof(choose)));

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }
}

/// <summary>Setup's persisted quest and hideout recommendation look-ahead controls.</summary>
public sealed class RecommendationHorizonSettingsViewModel : BindableViewModel
{
    private readonly RecommendationPolicyService _policies;

    public RecommendationHorizonSettingsViewModel(RecommendationPolicyService policies)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        QuestChoices = Choices("quests", horizon => ApplyAsync(Current with { Quest = horizon }));
        HideoutChoices = Choices("hideout", horizon => ApplyAsync(Current with { Hideout = horizon }));
        _policies.Changed += OnChanged;
        Refresh();
    }

    public string Heading => SetupText.HorizonHeading;

    public string Hint => SetupText.HorizonHint;

    public string QuestLabel => SetupText.HorizonQuestLabel;

    public string HideoutLabel => SetupText.HorizonHideoutLabel;

    public RecommendationHorizonSettings Current => _policies.CurrentSettings;

    public IReadOnlyList<RecommendationHorizonChoiceViewModel> QuestChoices { get; }

    public IReadOnlyList<RecommendationHorizonChoiceViewModel> HideoutChoices { get; }

    private static IReadOnlyList<RecommendationHorizonChoiceViewModel> Choices(
        string scope,
        Action<RecommendationHorizon> choose) =>
    [
        new(RecommendationHorizon.NextOnly, SetupText.HorizonNextOnly, scope, () => choose(RecommendationHorizon.NextOnly)),
        new(RecommendationHorizon.NextThree, SetupText.HorizonNextThree, scope, () => choose(RecommendationHorizon.NextThree)),
        new(RecommendationHorizon.NextFive, SetupText.HorizonNextFive, scope, () => choose(RecommendationHorizon.NextFive)),
        new(RecommendationHorizon.All, SetupText.HorizonAll, scope, () => choose(RecommendationHorizon.All)),
    ];

    private void ApplyAsync(RecommendationHorizonSettings settings) =>
        _policies.UpdateAsync(settings, CancellationToken.None).Observe("setup", "save recommendation horizons");

    private void OnChanged(object? sender, RecommendationHorizonSettings settings) => Refresh();

    private void Refresh()
    {
        Mark(QuestChoices, Current.Quest);
        Mark(HideoutChoices, Current.Hideout);
        OnPropertyChanged(nameof(Current));
    }

    private static void Mark(
        IReadOnlyList<RecommendationHorizonChoiceViewModel> choices,
        RecommendationHorizon current)
    {
        foreach (var choice in choices)
        {
            choice.IsCurrent = choice.Horizon == current;
        }
    }
}
