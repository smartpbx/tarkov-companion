using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// "This did not load", said where the player is looking, with a button that tries again.
/// </summary>
/// <remarks>
/// #453, "panes or tabs that do not render until a restart". A workspace that failed to load used
/// to leave one grey sentence in its header and an unlabelled refresh icon; the pane under it was
/// simply empty, which reads as the application being broken rather than as one read having
/// failed. This is the same state made legible: what did not load, and Retry.
///
/// One class for both places it appears — inside a workspace, and across the top of the shell for
/// pages that failed at startup — so the two cannot drift into saying it differently.
/// </remarks>
public sealed class LoadFaultNoticeViewModel : BindableViewModel
{
    private readonly Func<Task> _retry;
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private bool _isVisible;
    private bool _isRetrying;

    public LoadFaultNoticeViewModel(Func<Task> retry)
    {
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        RetryCommand = new AsyncDelegateCommand(RetryAsync);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    /// <summary>What did not load, in a few words: "Quests did not load".</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>What to expect next. Never the exception: that belongs in the log.</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsRetrying
    {
        get => _isRetrying;
        private set
        {
            if (SetProperty(ref _isRetrying, value))
            {
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(RetryLabel));
            }
        }
    }

    public bool CanRetry => !_isRetrying;

    public string RetryLabel => _isRetrying ? TarkovCompanion.App.Localization.ShellText.FaultRetrying : TarkovCompanion.App.Localization.ShellText.FaultRetry;

    public ICommand RetryCommand { get; }

    public void Show(string title, string detail)
    {
        Title = title;
        Detail = detail;
        IsVisible = true;
    }

    public void Clear() => IsVisible = false;

    /// <summary>Runs the load again. Whether the notice stays is the load's decision, not this one's.</summary>
    public async Task RetryAsync()
    {
        if (_isRetrying)
        {
            return;
        }

        IsRetrying = true;
        try
        {
            await _retry().ConfigureAwait(true);
        }
        finally
        {
            IsRetrying = false;
        }
    }
}
