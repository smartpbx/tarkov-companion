using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>One answer button on the after-raid card, in <see cref="RaidOutcomeQuestion.Choices"/> order.</summary>
public sealed record RaidOutcomeChoiceViewModel(RaidOutcomeBucket Bucket, string Label, ICommand Command);

/// <summary>One recap fact and where it came from.</summary>
/// <param name="Icon">The V2 icon resource suffix (Clock, People, Exit, Box, Clipboard).</param>
/// <param name="KindLabel">Observed, Inferred, Estimate or Manual; empty where there is nothing to label.</param>
public sealed record RaidRecapLineViewModel(string Icon, string Text, string KindLabel)
{
    public bool HasKind => KindLabel.Length > 0;
}

/// <summary>
/// The after-raid card (#712 0-8, docs/design/v3/v3-post-raid-outcome.png): one quiet question,
/// "How did the raid end?", answered with one tap, then a recap with the source of every fact.
/// </summary>
/// <remarks>
/// A card in the page, not a dialog and never anything over the game. It asks once per raid:
/// the owner (<see cref="DebriefWorkspaceViewModel"/>) decides whether to ask, stores the answer,
/// and records the dismissal so a raid is not asked about twice. What this holds is presentation.
/// </remarks>
public sealed class RaidOutcomeCardViewModel : BindableViewModel
{
    private readonly Func<RaidOutcomeBucket, Task> _answer;
    private readonly Func<Task> _later;
    private bool _isVisible;
    private bool _isAsking;
    private Guid? _raidId;
    private string _endedLabel = string.Empty;
    private string _outcomeLabel = string.Empty;
    private string _outcomeKindLabel = string.Empty;
    private string _status = string.Empty;
    private IReadOnlyList<RaidRecapLineViewModel> _recap = [];

    public RaidOutcomeCardViewModel(Func<RaidOutcomeBucket, Task> answer, Func<Task> later)
    {
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));
        _later = later ?? throw new ArgumentNullException(nameof(later));
        Choices =
        [
            .. RaidOutcomeQuestion.Choices.Select(bucket => new RaidOutcomeChoiceViewModel(
                bucket,
                DebriefText.Answer(bucket),
                new AsyncDelegateCommand(() => _answer(bucket)))),
        ];
        LaterCommand = new AsyncDelegateCommand(() => _later());
        CloseCommand = new DelegateCommand(() => IsVisible = false);
    }

    public IReadOnlyList<RaidOutcomeChoiceViewModel> Choices { get; }

    public ICommand LaterCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>The raid the card is about, or null before any raid has ended this session.</summary>
    public Guid? RaidId => _raidId;

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    /// <summary>Whether the question is still open for this raid.</summary>
    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (SetProperty(ref _isAsking, value))
            {
                OnPropertyChanged(nameof(ShowsOutcome));
            }
        }
    }

    /// <summary>The outcome line, once there is no question left to ask.</summary>
    public bool ShowsOutcome => !_isAsking;

    public string EndedLabel { get => _endedLabel; private set => SetProperty(ref _endedLabel, value); }

    /// <summary>"Survived · you said so at 20:53", or "Outcome not recorded"; never a guess.</summary>
    public string OutcomeLabel { get => _outcomeLabel; private set => SetProperty(ref _outcomeLabel, value); }

    public string OutcomeKindLabel
    {
        get => _outcomeKindLabel;
        private set
        {
            if (SetProperty(ref _outcomeKindLabel, value))
            {
                OnPropertyChanged(nameof(HasOutcomeKind));
            }
        }
    }

    public bool HasOutcomeKind => _outcomeKindLabel.Length > 0;

    /// <summary>Why an answer was not saved, or empty.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    public IReadOnlyList<RaidRecapLineViewModel> Recap
    {
        get => _recap;
        set
        {
            if (SetProperty(ref _recap, value))
            {
                OnPropertyChanged(nameof(HasRecap));
            }
        }
    }

    public bool HasRecap => _recap.Count > 0;

    /// <summary>A raid just ended: show the card, with the question when it is to be asked.</summary>
    public void Show(Guid raidId, string endedLabel, bool ask, string? recordedOutcome, string recordedKindLabel)
    {
        _raidId = raidId;
        Status = string.Empty;
        Recap = [];
        EndedLabel = endedLabel;
        IsAsking = ask;
        SetOutcome(recordedOutcome, recordedKindLabel);
        IsVisible = true;
    }

    /// <summary>The question is closed: answered (with its label and source) or dismissed (null).</summary>
    public void Close(string? outcomeLabel, string kindLabel)
    {
        IsAsking = false;
        Status = string.Empty;
        SetOutcome(outcomeLabel, kindLabel);
    }

    private void SetOutcome(string? outcome, string kindLabel)
    {
        OutcomeLabel = string.IsNullOrWhiteSpace(outcome) ? DebriefText.OutcomeNotRecorded : outcome;
        OutcomeKindLabel = string.IsNullOrWhiteSpace(outcome) ? string.Empty : kindLabel;
    }
}
