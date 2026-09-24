using TarkovCompanion.App.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// Setup › Privacy › screenshot tidying (#309): see exactly what it would do before turning it on, run it as a
/// dry run any time, turn it off in one press, and read what it did last.
/// </summary>
/// <remarks>
/// Turning tidying <em>on</em> is a two-step: press, read the preview, confirm. It deletes files the player
/// did not make, so the consent is given to a list rather than to a switch. Turning it off is one press.
/// The preview and the real run are the same planning code (<see cref="ScreenshotRetentionService.Run"/>
/// with <c>dryRun: true</c>), so the list is what would happen, not a second opinion about it. The actual
/// on/off write stays with the Settings view model's own command, so there is one writer of the setting.
/// </remarks>
public sealed class SetupCleanupViewModel : BindableViewModel
{
    private const int FilesShown = 8;

    private readonly ScreenshotRetentionService _service;
    private readonly IScreenshotRetentionStore _store;
    private readonly IScreenshotTidyLedger? _ledger;
    private readonly Func<string?> _screenshotRoot;
    private readonly Func<ICommand?> _toggle;
    private readonly Func<bool> _canTidy;
    private readonly TimeProvider _clock;
    private bool _isConfirming;
    private bool _hasPreview;
    private string _previewFolder = string.Empty;
    private string _previewPolicy = string.Empty;
    private string _previewSummary = string.Empty;
    private string _previewExcluded = string.Empty;
    private string _previewRefusal = string.Empty;
    private bool _isEnabled;

    public SetupCleanupViewModel(
        ScreenshotRetentionService service,
        IScreenshotRetentionStore store,
        IScreenshotTidyLedger? ledger,
        Func<string?> screenshotRoot,
        Func<ICommand?> toggle,
        Func<bool> canTidy,
        TimeProvider clock)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ledger = ledger;
        _screenshotRoot = screenshotRoot ?? throw new ArgumentNullException(nameof(screenshotRoot));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _canTidy = canTidy ?? throw new ArgumentNullException(nameof(canTidy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PreviewCommand = new AsyncDelegateCommand(() => PreviewAsync(confirming: false));
        RequestToggleCommand = new AsyncDelegateCommand(RequestToggleAsync);
        ConfirmEnableCommand = new DelegateCommand(ConfirmEnable);
        CancelCommand = new DelegateCommand(Cancel);
        RefreshLedger();
    }

    public ObservableCollection<string> PreviewFiles { get; } = [];

    public ObservableCollection<string> LastRuns { get; } = [];

    public ICommand PreviewCommand { get; }

    public ICommand RequestToggleCommand { get; }

    public ICommand ConfirmEnableCommand { get; }

    public ICommand CancelCommand { get; }

    public string PreviewLabel => SetupText.CleanupPreview;
    public string ConfirmLabel => SetupText.CleanupConfirm;
    public string CancelLabel => SetupText.CleanupCancel;
    public string FolderLabel => SetupText.CleanupFolder;
    public string LedgerHeading => SetupText.CleanupLedgerHeading;
    public string LedgerEmpty => SetupText.CleanupLedgerEmpty;

    public bool CanTidy => _canTidy();

    /// <summary>What the toggle button says: it starts tidying from a preview, or stops it at once.</summary>
    public string ToggleLabel => _isEnabled ? SetupText.CleanupStop : SetupText.CleanupStart;

    public bool IsConfirming
    {
        get => _isConfirming;
        private set => SetProperty(ref _isConfirming, value);
    }

    public bool HasPreview
    {
        get => _hasPreview;
        private set => SetProperty(ref _hasPreview, value);
    }

    public string PreviewFolder
    {
        get => _previewFolder;
        private set => SetProperty(ref _previewFolder, value);
    }

    public string PreviewPolicy
    {
        get => _previewPolicy;
        private set => SetProperty(ref _previewPolicy, value);
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetProperty(ref _previewSummary, value);
    }

    public string PreviewExcluded
    {
        get => _previewExcluded;
        private set
        {
            if (SetProperty(ref _previewExcluded, value))
            {
                OnPropertyChanged(nameof(HasPreviewExcluded));
            }
        }
    }

    public bool HasPreviewExcluded => PreviewExcluded.Length > 0;

    /// <summary>Why there is nothing to preview: no folder yet, a drive root, a folder that is not there.</summary>
    public string PreviewRefusal
    {
        get => _previewRefusal;
        private set
        {
            if (SetProperty(ref _previewRefusal, value))
            {
                OnPropertyChanged(nameof(IsRefused));
            }
        }
    }

    public bool IsRefused => PreviewRefusal.Length > 0;

    public bool HasLedger => LastRuns.Count > 0;

    public bool HasNoLedger => LastRuns.Count == 0;

    /// <summary>Reads the ledger again; called when the section opens and after every run this page starts.</summary>
    public void RefreshLedger()
    {
        LastRuns.Clear();
        foreach (var entry in (_ledger?.Read() ?? []).OrderByDescending(entry => entry.AtUtc).Take(5))
        {
            LastRuns.Add(DescribeEntry(entry));
            foreach (var (reason, count) in entry.FailureReasons.OrderByDescending(pair => pair.Value))
            {
                LastRuns.Add(string.Create(CultureInfo.CurrentCulture, $"   {count} × {reason}"));
            }
        }

        OnPropertyChanged(nameof(HasLedger));
        OnPropertyChanged(nameof(HasNoLedger));
    }

    /// <summary>Loads whether tidying is on, so the button says the right thing.</summary>
    public async Task LoadAsync()
    {
        try
        {
            _isEnabled = (await _store.GetAsync(CancellationToken.None).ConfigureAwait(true)).IsEnabled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isEnabled = false;
        }

        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(CanTidy));
    }

    private async Task RequestToggleAsync()
    {
        await LoadAsync().ConfigureAwait(true);
        if (_isEnabled)
        {
            // One press to stop. A confirmation on the way out would make the safe direction the slow one.
            _toggle()?.Execute(null);
            _isEnabled = false;
            Cancel();
            OnPropertyChanged(nameof(ToggleLabel));
            return;
        }

        await PreviewAsync(confirming: true).ConfigureAwait(true);
    }

    private async Task PreviewAsync(bool confirming)
    {
        ScreenshotRetentionSettings settings;
        try
        {
            settings = await _store.GetAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            settings = ScreenshotRetentionSettings.Default;
        }

        var root = _screenshotRoot();
        // The same call the hourly tidy makes, stopped before the first move.
        var result = _service.Run(root ?? string.Empty, settings, dryRun: true);
        var plan = result.Plan;
        PreviewFiles.Clear();
        PreviewFolder = plan.Root;
        PreviewRefusal = plan.Refusal ?? string.Empty;
        PreviewPolicy = SetupText.CleanupPolicy(plan.RetentionHours);
        if (plan.IsRefused)
        {
            PreviewSummary = string.Empty;
            PreviewExcluded = string.Empty;
        }
        else
        {
            PreviewSummary = plan.Count switch
            {
                0 => SetupText.CleanupSummaryNone,
                1 => SetupText.CleanupSummaryOne(FormatBytes(plan.TotalBytes)),
                _ => SetupText.CleanupSummary(plan.Count, FormatBytes(plan.TotalBytes)),
            };
            foreach (var file in plan.Eligible.Take(FilesShown))
            {
                PreviewFiles.Add(file.Name);
            }

            if (plan.Count > FilesShown)
            {
                PreviewFiles.Add(SetupText.CleanupMoreFiles(plan.Count - FilesShown));
            }

            PreviewExcluded = DescribeExcluded(plan);
        }

        HasPreview = true;
        // Only a folder that can actually be tidied is offered for confirmation.
        IsConfirming = confirming && !plan.IsRefused && CanTidy;
    }

    private void ConfirmEnable()
    {
        _toggle()?.Execute(null);
        _isEnabled = true;
        IsConfirming = false;
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void Cancel()
    {
        IsConfirming = false;
        HasPreview = false;
    }

    private static string DescribeExcluded(ScreenshotTidyPlan plan)
    {
        var parts = new List<string>();
        Add(TidySkipReason.TooRecent, "V2.Setup.Cleanup.ExTooRecent");
        Add(TidySkipReason.CloudPlaceholder, "V2.Setup.Cleanup.ExCloud");
        Add(TidySkipReason.LinkOrReparsePoint, "V2.Setup.Cleanup.ExLink");
        if (plan.NotGameFiles > 0)
        {
            parts.Add(SetupText.CleanupExOther(plan.NotGameFiles));
        }

        return parts.Count == 0 ? string.Empty : SetupText.CleanupExcluded(string.Join(", ", parts));

        void Add(TidySkipReason reason, string key)
        {
            if (plan.ExcludedCount(reason) is > 0 and var count)
            {
                parts.Add(V2ShellText.Format(key, CultureInfo.CurrentCulture, count));
            }
        }
    }

    private string DescribeEntry(TidyLedgerEntry entry) => SetupText.CleanupLedgerEntry(V2ShellText.Age(entry.AtUtc, _clock.GetUtcNow(), CultureInfo.CurrentCulture), entry.Moved, FormatBytes(entry.MovedBytes), entry.Failed);

    /// <summary>Bytes as a person reads them: 38.2 MB, not 40054812.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1_048_576 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_024.0:0.#} KB"),
        < 1_073_741_824 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_048_576.0:0.#} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1_073_741_824.0:0.##} GB"),
    };
}
