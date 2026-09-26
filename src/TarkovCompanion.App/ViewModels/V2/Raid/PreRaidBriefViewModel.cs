namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#712 0-9] The brief the Raid panel shows while matching; the Now panel can host the same view.</summary>
public sealed class PreRaidBriefViewModel : BindableViewModel
{
    private PreRaidBrief _current = PreRaidBrief.Hidden;

    public PreRaidBrief Current
    {
        get => _current;
        private set
        {
            if (SetProperty(ref _current, value))
            {
                OnPropertyChanged(nameof(IsShown));
            }
        }
    }

    public bool IsShown => _current.IsShown;

    /// <summary>Replaces the brief; an equal one raises nothing, so a steady queue does not redraw.</summary>
    public void Show(PreRaidBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (!Same(_current, brief))
        {
            Current = brief;
        }
    }

    private static bool Same(PreRaidBrief a, PreRaidBrief b) =>
        a.IsShown == b.IsShown && a.Kicker == b.Kicker && a.Title == b.Title && a.Bosses == b.Bosses &&
        a.Quests.SequenceEqual(b.Quests) && a.QuestsNote == b.QuestsNote && a.Route == b.Route &&
        a.CanStillLeave == b.CanStillLeave && a.ExtractsHeading == b.ExtractsHeading && a.Extracts == b.Extracts &&
        a.ExtractRequirements.SequenceEqual(b.ExtractRequirements) && a.Squad.SequenceEqual(b.Squad) &&
        a.Loot == b.Loot && a.Because == b.Because;
}
