using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One exit of the current map, and whether this raid offers it.</summary>
public sealed class RaidExtractChoiceViewModel(string name, bool isOffered, Action<string> toggle)
{
    public string Name { get; } = name;

    public bool IsOffered { get; } = isOffered;

    public ICommand ToggleCommand { get; } = new DelegateCommand(() => toggle(name));

    public string AutomationId => "v2-raid-correct-extract-" + Name.ToLowerInvariant().Replace(' ', '-');
}

/// <summary>
/// The Raid plan's "Corrections" card (#286): the side, the clock and the offered exits, each
/// saying whether it was read or set by hand, each with its way back to what was read.
/// </summary>
/// <remarks>
/// This holds no raid state. It writes to <see cref="RaidManualCorrections"/>, which every page's
/// snapshot passes through, and redraws from the corrected snapshot the cockpit hands it; the
/// clock it shows is the one <see cref="RaidPageViewModel.Clock"/>, not a second count.
/// </remarks>
public sealed class RaidCorrectionsViewModel : BindableViewModel
{
    private const string Automatic = "Automatic";
    private const string Manual = "Manual";

    private readonly RaidManualCorrections _corrections;
    private readonly TimeProvider _time;
    private RaidSnapshot? _raid;
    private IReadOnlyList<string> _extractNames = [];
    private string _timeLeftInput = string.Empty;
    private string _clockText = string.Empty;
    private string _inputNote = string.Empty;
    private bool _isOpen;

    public RaidCorrectionsViewModel(RaidManualCorrections corrections, TimeProvider? time = null)
    {
        _corrections = corrections ?? throw new ArgumentNullException(nameof(corrections));
        _time = time ?? TimeProvider.System;
        SetPmcCommand = new DelegateCommand(() => SetSide("PMC"));
        SetScavCommand = new DelegateCommand(() => SetSide("Scav"));
        SetTimeLeftCommand = new DelegateCommand(SetTimeLeft);
        StartedNowCommand = new DelegateCommand(() => WhenInRaid(() => _corrections.SetStarted(_time.GetUtcNow())));
        ReturnSideCommand = new DelegateCommand(() => _corrections.ReturnToAutomatic(RaidCorrectionField.Side));
        ReturnClockCommand = new DelegateCommand(() => _corrections.ReturnToAutomatic(RaidCorrectionField.Clock));
        ReturnExtractsCommand = new DelegateCommand(() => _corrections.ReturnToAutomatic(RaidCorrectionField.Extracts));
    }

    public ICommand SetPmcCommand { get; }

    public ICommand SetScavCommand { get; }

    public ICommand SetTimeLeftCommand { get; }

    public ICommand StartedNowCommand { get; }

    public ICommand ReturnSideCommand { get; }

    public ICommand ReturnClockCommand { get; }

    public ICommand ReturnExtractsCommand { get; }

    public ObservableCollection<RaidExtractChoiceViewModel> ExtractChoices { get; } = [];

    /// <summary>Whether the card is open. Closed by default: most raids need no correcting.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    /// <summary>What the closed card says: how much of this raid is the player's own word.</summary>
    public string Summary
    {
        get
        {
            var manual = new[] { SideIsManual, ClockIsManual, ExtractsIsManual }.Count(item => item);
            return !IsInRaid ? "During a raid" : manual == 0 ? "All automatic" : $"{manual} set by you";
        }
    }

    public bool IsInRaid => _raid?.State == RaidLifecycleState.InRaid;

    public bool IsNotInRaid => !IsInRaid;

    public bool IsPmc => SideOf(_raid?.Side) == "PMC";

    public bool IsScav => SideOf(_raid?.Side) == "Scav";

    public string SideText => SideOf(_raid?.Side) ?? "Unknown";

    public bool SideIsManual => IsInRaid && _corrections.IsManual(RaidCorrectionField.Side);

    public string SideSource => SideIsManual ? Manual : Automatic;

    public string ClockText => _clockText.Length > 0 ? _clockText : "Unknown";

    public bool ClockIsManual => IsInRaid && _corrections.IsManual(RaidCorrectionField.Clock);

    public string ClockSource => ClockIsManual ? Manual : Automatic;

    public bool ExtractsIsManual => IsInRaid && _corrections.IsManual(RaidCorrectionField.Extracts);

    public string ExtractsSource => ExtractsIsManual ? Manual : Automatic;

    public bool HasExtractChoices => ExtractChoices.Count > 0;

    public string ExtractsSummary
    {
        get
        {
            var offered = ExtractChoices.Count(choice => choice.IsOffered);
            return offered == 0 ? "None marked offered" : $"{offered} offered";
        }
    }

    /// <summary>What the player is typing for the time left, as the game shows it.</summary>
    public string TimeLeftInput
    {
        get => _timeLeftInput;
        set => SetProperty(ref _timeLeftInput, value ?? string.Empty);
    }

    public string InputNote
    {
        get => _inputNote;
        private set
        {
            if (SetProperty(ref _inputNote, value))
            {
                OnPropertyChanged(nameof(HasInputNote));
            }
        }
    }

    public bool HasInputNote => InputNote.Length > 0;

    /// <summary>The clock text changed; it is the Raid page's one clock, repeated here.</summary>
    public void ShowClock(string clock)
    {
        SetProperty(ref _clockText, clock ?? string.Empty, nameof(ClockText));
    }

    /// <summary>Redraws from the corrected snapshot and the exits the current map lists.</summary>
    public void Refresh(RaidSnapshot raid, IReadOnlyList<string> extractNames)
    {
        _raid = raid ?? throw new ArgumentNullException(nameof(raid));
        _extractNames = extractNames ?? [];
        ExtractChoices.Clear();
        if (IsInRaid)
        {
            foreach (var name in _extractNames)
            {
                ExtractChoices.Add(new(name, MapViewModel.IsOfferedMarker(name, raid.ActiveExtracts), ToggleExtract));
            }
        }

        foreach (var property in new[]
                 {
                     nameof(IsInRaid), nameof(IsNotInRaid), nameof(IsPmc), nameof(IsScav), nameof(SideText),
                     nameof(SideIsManual), nameof(SideSource), nameof(ClockIsManual), nameof(ClockSource),
                     nameof(ExtractsIsManual), nameof(ExtractsSource), nameof(HasExtractChoices), nameof(ExtractsSummary),
                     nameof(Summary),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private void SetSide(string side) => WhenInRaid(() => _corrections.SetSide(side));

    private void SetTimeLeft()
    {
        if (!RaidManualCorrections.TryParseTimeLeft(TimeLeftInput, out var left))
        {
            InputNote = "Type the time left as minutes:seconds, like 23:10";
            return;
        }

        WhenInRaid(() =>
        {
            _corrections.SetTimeLeft(left, _time.GetUtcNow());
            TimeLeftInput = string.Empty;
        });
    }

    private void ToggleExtract(string name) => WhenInRaid(() =>
    {
        var offered = ExtractChoices.Where(choice => choice.IsOffered).Select(choice => choice.Name).ToList();
        if (offered.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            offered.Add(name);
        }

        _corrections.SetExtracts(offered);
    });

    private void WhenInRaid(Action correct)
    {
        if (!IsInRaid)
        {
            InputNote = "No raid in progress";
            return;
        }

        InputNote = string.Empty;
        correct();
    }

    private static string? SideOf(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "scav" or "savage" => "Scav",
        "pmc" or "usec" or "bear" => "PMC",
        _ => null,
    };
}
