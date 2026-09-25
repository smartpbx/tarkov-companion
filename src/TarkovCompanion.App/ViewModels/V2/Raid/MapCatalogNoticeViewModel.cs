using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#292] The Raid page's "there is no map list" card: what happened and the one thing to do.
/// </summary>
/// <remarks>
/// Local only is a choice, so its button goes to the switch that undoes it rather than offering a
/// Retry that cannot succeed. Any other failure offers Retry. Neither shows the exception: that is
/// in the log (the same pattern as the page load faults, #876).
/// </remarks>
public sealed class MapCatalogNoticeViewModel : BindableViewModel
{
    private readonly Func<Task> _retry;
    private readonly Action _openDataPrivacy;
    private MapCatalogState _state = MapCatalogState.Loading;
    private bool _isRetrying;

    public MapCatalogNoticeViewModel(Func<Task> retry, Action openDataPrivacy)
    {
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _openDataPrivacy = openDataPrivacy ?? throw new ArgumentNullException(nameof(openDataPrivacy));
        ActionCommand = new AsyncDelegateCommand(ActAsync);
    }

    public MapCatalogState State => _state;

    public bool IsVisible => _state is MapCatalogState.LocalOnly or MapCatalogState.Failed;

    public string Title => MapCatalogStates.Title(_state);

    public string Detail => MapCatalogStates.Detail(_state);

    public string ActionLabel => _state == MapCatalogState.LocalOnly
        ? RaidText.MapCatalogOpenDataPrivacy
        : _isRetrying ? ShellText.FaultRetrying : ShellText.FaultRetry;

    public bool CanAct => !_isRetrying;

    public ICommand ActionCommand { get; }

    public void Apply(MapCatalogState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ActionLabel));
    }

    /// <summary>Opens Data & Privacy under Local only; otherwise asks for the map list again.</summary>
    public async Task ActAsync()
    {
        if (_state == MapCatalogState.LocalOnly)
        {
            _openDataPrivacy();
            return;
        }

        if (_isRetrying)
        {
            return;
        }

        SetRetrying(true);
        try
        {
            await _retry().ConfigureAwait(true);
        }
        finally
        {
            SetRetrying(false);
        }
    }

    private void SetRetrying(bool value)
    {
        _isRetrying = value;
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(ActionLabel));
    }
}
