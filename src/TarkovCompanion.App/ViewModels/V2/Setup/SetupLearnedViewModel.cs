using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Domain.Recognition.Learning;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// Setup's row for what the companion learned from corrections (#712 1-12, decision 6): the
/// counts, the switch for keeping icon crops (on by default), Delete all and a clear per kind.
/// </summary>
public sealed class SetupLearnedViewModel : BindableViewModel
{
    private readonly CorrectionMemory _memory;
    private readonly Action<Action> _post;
    private CorrectionMemoryCounts _counts = CorrectionMemoryCounts.None;
    private string _status = string.Empty;

    public SetupLearnedViewModel(CorrectionMemory memory, Action<Action>? post = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _post = post ?? (action => action());
        DeleteAllCommand = new AsyncDelegateCommand(() => RunAsync(token => _memory.ClearAllAsync(token)));
        ClearIconsCommand = new AsyncDelegateCommand(() => RunAsync(token => _memory.ClearAsync(CorrectionMemoryKind.Icons, token)));
        ClearNamesCommand = new AsyncDelegateCommand(() => RunAsync(token => _memory.ClearAsync(CorrectionMemoryKind.Names, token)));
        _memory.Changed += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    public string Heading => LearnText.Heading;

    public string CountsLabel => LearnText.Counts(_counts.Icons, _counts.Names);

    public bool HasAnything => _counts.Icons + _counts.Names + _counts.FrameCorrections > 0;

    public string KeepCropsLabel => LearnText.KeepCrops;

    public string KeepCropsNote => LearnText.KeepCropsNote;

    public bool KeepsIconCrops
    {
        get => _memory.KeepsIconCrops;
        set
        {
            if (_memory.KeepsIconCrops != value)
            {
                _memory.KeepsIconCrops = value;
                OnPropertyChanged();
            }
        }
    }

    public string DeleteAllLabel => LearnText.DeleteAll;

    public string ClearIconsLabel => LearnText.ClearIcons;

    public string ClearNamesLabel => LearnText.ClearNames;

    public ICommand DeleteAllCommand { get; }

    public ICommand ClearIconsCommand { get; }

    public ICommand ClearNamesCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Reads the counts again; public so a test can wait for it.</summary>
    public async Task RefreshAsync()
    {
        var counts = await _memory.CountAsync(CancellationToken.None).ConfigureAwait(false);
        _post(() =>
        {
            _counts = counts;
            OnPropertyChanged(nameof(CountsLabel));
            OnPropertyChanged(nameof(HasAnything));
            OnPropertyChanged(nameof(KeepsIconCrops));
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> clear)
    {
        try
        {
            await clear(CancellationToken.None).ConfigureAwait(false);
            _post(() => Status = LearnText.Deleted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _post(() => Status = LearnText.DeleteFailed(exception.Message));
        }

        await RefreshAsync().ConfigureAwait(false);
    }
}
