using System.ComponentModel;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// The one switch that decides whether Setup shows file paths as they are, or with the part that names a
/// person left out. It starts hidden, every launch, and nothing persists it.
/// </summary>
public sealed class SetupPathDisclosureViewModel : BindableViewModel
{
    private readonly string? _userProfile;
    // Shown in full from the start. They were masked behind a "Show file paths" switch that sat above
    // every Setup section and pushed the overview off one screen; the owner's verdict was "just show
    // the file paths, who cares". This is his own PC. What leaves the machine (the problem report)
    // is still masked, by SetupPathMask, regardless of this.
    private bool _isRevealed = true;

    public SetupPathDisclosureViewModel(string? userProfile = null)
    {
        _userProfile = userProfile;
        ToggleCommand = new DelegateCommand(() => IsRevealed = !IsRevealed);
    }

    public bool IsRevealed
    {
        get => _isRevealed;
        private set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(Note));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ToggleLabel => IsRevealed ? V2ShellText.Get("V2.Setup.Paths.Hide") : V2ShellText.Get("V2.Setup.Paths.Show");

    /// <summary>One line, and only while paths are showing: that is the moment it is useful.</summary>
    public string Note => IsRevealed ? V2ShellText.Get("V2.Setup.Paths.RevealedNote") : V2ShellText.Get("V2.Setup.Paths.HiddenNote");

    public ICommand ToggleCommand { get; }

    /// <summary>Raised when the switch flips, for text that was derived from it.</summary>
    public event EventHandler? Changed;

    public string Apply(string? text) => IsRevealed ? text ?? string.Empty : SetupPathMask.Mask(text, _userProfile);
}

/// <summary>A line of text that may hold paths, shown through <see cref="SetupPathDisclosureViewModel"/>.</summary>
public sealed class GatedPathText : BindableViewModel, IDisposable
{
    private readonly Func<string?> _source;
    private readonly SetupPathDisclosureViewModel _gate;
    private readonly INotifyPropertyChanged? _owner;
    private readonly string _sourceProperty;
    private string _text = string.Empty;

    /// <param name="source">Reads the raw line.</param>
    /// <param name="gate">The switch that decides whether paths in it show.</param>
    /// <param name="owner">What raises <see cref="INotifyPropertyChanged.PropertyChanged"/> when the raw line changes.</param>
    /// <param name="sourceProperty">The property on <paramref name="owner"/> the raw line comes from.</param>
    public GatedPathText(
        Func<string?> source,
        SetupPathDisclosureViewModel gate,
        INotifyPropertyChanged? owner = null,
        string sourceProperty = "")
    {
        _source = source;
        _gate = gate;
        _owner = owner;
        _sourceProperty = sourceProperty;
        _gate.Changed += OnChanged;
        if (_owner is not null)
        {
            _owner.PropertyChanged += OnOwnerChanged;
        }

        Refresh();
    }

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public void Dispose()
    {
        _gate.Changed -= OnChanged;
        if (_owner is not null)
        {
            _owner.PropertyChanged -= OnOwnerChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == _sourceProperty)
        {
            Refresh();
        }
    }

    private void Refresh() => Text = _gate.Apply(_source());
}
