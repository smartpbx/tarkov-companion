using System.Windows.Input;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>
/// [#712 0-4] The Raid page's right column: the Now panel, or the Raid plan cards as its More drawer.
/// </summary>
/// <remarks>
/// The sixteen cards are not deleted (#712 T3: "nothing V2 could show is lost"). With the
/// <c>now-panel</c> flag on they are the More drawer, one press away and back again; with it off,
/// or before a situation exists to project, they are the column as they always were.
/// </remarks>
public sealed class NowPanelHost : BindableViewModel
{
    /// <summary>Which More topic each Raid plan card (<see cref="Raid.RaidPanelCards"/> id) belongs to.</summary>
    public static readonly IReadOnlyDictionary<string, NowMoreTopic> TopicOfCard = new Dictionary<string, NowMoreTopic>(StringComparer.Ordinal)
    {
        ["summary"] = NowMoreTopic.Traffic,
        ["squad"] = NowMoreTopic.Squad,
        ["objectives"] = NowMoreTopic.Objectives,
        ["extracts"] = NowMoreTopic.Extracts,
        ["extract-selection"] = NowMoreTopic.Extracts,
        ["route"] = NowMoreTopic.Traffic,
        ["marks"] = NowMoreTopic.Marks,
        ["group-marks"] = NowMoreTopic.Marks,
        ["tasks"] = NowMoreTopic.Objectives,
        ["loot-selection"] = NowMoreTopic.Loot,
        ["spawn-areas"] = NowMoreTopic.Spawns,
        ["spawns"] = NowMoreTopic.Spawns,
        ["ways-out"] = NowMoreTopic.Extracts,
        ["loot-nearby"] = NowMoreTopic.Loot,
        ["loot-filters"] = NowMoreTopic.Loot,
        ["corrections"] = NowMoreTopic.Corrections,
    };

    private readonly Func<bool> _isFlagOn;
    private readonly Action<NowMoreTopic> _reveal;
    private NowPanelViewModel? _panel;
    private bool _isMoreOpen;

    public NowPanelHost(Func<bool> isFlagOn, Action<NowMoreTopic>? reveal = null)
    {
        _isFlagOn = isFlagOn ?? throw new ArgumentNullException(nameof(isFlagOn));
        _reveal = reveal ?? (_ => { });
        CloseMoreCommand = new DelegateCommand(CloseMore);
    }

    /// <summary>The More drawer opened at a topic: the view scrolls the cards to it.</summary>
    public event EventHandler<NowMoreTopic>? MoreOpened;

    public NowPanelViewModel? Panel
    {
        get => _panel;
        set
        {
            if (ReferenceEquals(_panel, value))
            {
                return;
            }

            if (_panel is not null)
            {
                _panel.MoreRequested -= PanelMoreRequested;
            }

            _panel = value;
            if (_panel is not null)
            {
                _panel.MoreRequested += PanelMoreRequested;
            }

            Changed();
        }
    }

    /// <summary>The flag is on and there is a situation to show: the column is the Now panel's.</summary>
    public bool IsNowOn => _panel is not null && _isFlagOn();

    public bool IsMoreOpen => _isMoreOpen && IsNowOn;

    public bool ShowsNowPanel => IsNowOn && !_isMoreOpen;

    /// <summary>The Raid plan cards: the whole column with the flag off, the More drawer with it on.</summary>
    public bool ShowsCardStack => !ShowsNowPanel;

    public ICommand CloseMoreCommand { get; }

    public string BackLabel => NowText.Back;

    public void OpenMore(NowMoreTopic topic = NowMoreTopic.All)
    {
        if (topic != NowMoreTopic.All)
        {
            _reveal(topic);
        }

        _isMoreOpen = true;
        Changed();
        MoreOpened?.Invoke(this, topic);
    }

    public void CloseMore()
    {
        _isMoreOpen = false;
        Changed();
    }

    /// <summary>The flag may have changed in Setup: lay the column out again.</summary>
    public void Refresh() => Changed();

    /// <summary>The card ids a topic opens, for the host to reveal.</summary>
    public static IEnumerable<string> CardsOf(NowMoreTopic topic) =>
        TopicOfCard.Where(pair => pair.Value == topic).Select(pair => pair.Key);

    /// <summary>Raised on every layout change, for the host's own dependent properties.</summary>
    public event EventHandler? LayoutChanged;

    private void PanelMoreRequested(object? sender, NowMoreTopic topic) => OpenMore(topic);

    private void Changed()
    {
        OnPropertyChanged(nameof(IsNowOn));
        OnPropertyChanged(nameof(IsMoreOpen));
        OnPropertyChanged(nameof(ShowsNowPanel));
        OnPropertyChanged(nameof(ShowsCardStack));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}
