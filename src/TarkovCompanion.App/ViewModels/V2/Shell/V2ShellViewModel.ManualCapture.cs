using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>How a picture the player chose arrived.</summary>
public enum V2ManualImageOrigin
{
    Paste = 1,
    Drop,
    Picker,
}

/// <summary>A picture the player chose: a file on disk, or pixels from the clipboard.</summary>
public sealed record V2ManualImage(V2ManualImageOrigin Origin, string? FilePath, CapturedImage? Pixels, ScanIntent Intent);

/// <summary>One named picture selected for a manual batch.</summary>
public sealed record V2ManualImageItem(string Id, string Label, string? FilePath, CapturedImage? Pixels);

/// <summary>Several pictures submitted together under one frozen intent and context.</summary>
public sealed record V2ManualImageBatch(
    string BatchId,
    V2ManualImageOrigin Origin,
    IReadOnlyList<V2ManualImageItem> Items,
    ScanIntent Intent);

public sealed record V2ManualImageBatchItemViewModel(
    string Id,
    string Label,
    string Status,
    bool IsTerminal,
    CaptureCorrelationId? CorrelationId = null)
{
    public string AutomationId => $"v2-shell-capture-batch-{Id}";
}

/// <summary>One thing a capture might have been, offered so a wrong answer can be put right.</summary>
public sealed class V2CaptureCandidateViewModel(V2CaptureCandidate candidate, bool isChosen, Action<V2CaptureCandidate> choose)
{
    public string Label { get; } = candidate.Confidence is { } confidence
        ? $"{candidate.DisplayName} · {confidence:P0}"
        : candidate.DisplayName;

    public bool IsChosen { get; } = isChosen;

    public string AutomationId { get; } = $"v2-shell-capture-candidate-{candidate.CanonicalId}";

    public ICommand ChooseCommand { get; } = new DelegateCommand(() => choose(candidate));
}

/// <summary>
/// [f920 capture] Manual image intake and the capture review's candidate list. Kept in its own
/// file so the shell's main file gains one line.
/// </summary>
public sealed partial class V2ShellViewModel
{
    private ICommand? _pickCaptureImageCommand;
    private ICommand? _cancelManualImageBatchCommand;
    private string _captureManualStatus = string.Empty;
    private string? _activeManualBatchId;
    private IReadOnlyList<V2ManualImageBatchItemViewModel> _captureBatchItems = [];
    private V2CaptureReview? _candidatesBuiltFor;
    private IReadOnlyList<V2CaptureCandidateViewModel> _captureReviewCandidates = [];

    /// <summary>Raised when the player pastes, drops or picks a picture. The capture bridge submits it.</summary>
    public event EventHandler<V2ManualImage>? ManualImageRequested;

    public event EventHandler<V2ManualImageBatch>? ManualImageBatchRequested;

    public event EventHandler<string>? ManualImageBatchCancelRequested;

    /// <summary>Raised when the player says a capture was a different candidate.</summary>
    public event EventHandler<V2CaptureCandidate>? CaptureCandidateChosen;

    /// <summary>Opens the platform's file picker. Set by the view, which owns the window.</summary>
    public Func<Task<IReadOnlyList<string>>>? CaptureImagePicker { get; set; }

    public string CaptureManualHeading => TarkovCompanion.App.Localization.ShellText.CaptureManualHeading;

    public string CaptureManualHint => TarkovCompanion.App.Localization.ShellText.CaptureManualHint;

    public string CapturePickLabel => TarkovCompanion.App.Localization.ShellText.CapturePickPictures;

    public string CaptureBatchCancelLabel => TarkovCompanion.App.Localization.ShellText.CaptureCancelRemaining;

    public string CaptureCandidatesHeading => TarkovCompanion.App.Localization.ShellText.CaptureCandidatesHeading;

    public string CaptureManualStatus
    {
        get => _captureManualStatus;
        private set
        {
            if (SetProperty(ref _captureManualStatus, value))
            {
                OnPropertyChanged(nameof(HasCaptureManualStatus));
            }
        }
    }

    public bool HasCaptureManualStatus => CaptureManualStatus.Length > 0;

    public ICommand PickCaptureImageCommand => _pickCaptureImageCommand ??= new AsyncDelegateCommand(PickCaptureImageAsync);

    public ICommand CancelManualImageBatchCommand =>
        _cancelManualImageBatchCommand ??= new DelegateCommand(CancelManualImageBatch);

    public IReadOnlyList<V2ManualImageBatchItemViewModel> CaptureBatchItems => _captureBatchItems;

    public bool HasCaptureBatchItems => CaptureBatchItems.Count > 0;

    public bool HasActiveManualImageBatch => _activeManualBatchId is not null
        && CaptureBatchItems.Any(item => !item.IsTerminal);

    /// <summary>What else the reviewed capture might have been, the answer first.</summary>
    public IReadOnlyList<V2CaptureCandidateViewModel> CaptureReviewCandidates
    {
        get
        {
            var review = CaptureState.Review;
            if (!ReferenceEquals(review, _candidatesBuiltFor))
            {
                _candidatesBuiltFor = review;
                _captureReviewCandidates = review is null || review.Candidates.Count < 2
                    ? []
                    : [.. review.Candidates.Select(candidate => new V2CaptureCandidateViewModel(
                        candidate,
                        string.Equals(candidate.CanonicalId, review.ChosenCandidateId, StringComparison.Ordinal),
                        chosen => CaptureCandidateChosen?.Invoke(this, chosen)))];
            }

            return _captureReviewCandidates;
        }
    }

    public bool HasCaptureReviewCandidates => CaptureReviewCandidates.Count > 0;

    /// <summary>Hands a picture to whoever submits captures, under the intent selected in the panel.</summary>
    public void SubmitManualImage(V2ManualImageOrigin origin, string? filePath, CapturedImage? pixels)
    {
        if (ManualImageRequested is not { } requested)
        {
            CaptureManualStatus = V2ShellText.Get("V2.Shell.Announce.CaptureUnavailable");
            return;
        }

        if (!TarkovCompanion.App.Services.V2.Capture.CaptureIntentSupport.IsSupported(SelectedCaptureIntent))
        {
            CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureIntentNotSupported(IntentLabel(SelectedCaptureIntent));
            return;
        }

        requested(this, new(origin, filePath, pixels, SelectedCaptureIntent));
    }

    /// <summary>Hands an ordered image batch to the capture bridge under the selected intent.</summary>
    public void SubmitManualImages(V2ManualImageOrigin origin, IReadOnlyList<V2ManualImageItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureNoPicturesSelected;
            return;
        }

        if (ManualImageBatchRequested is not { } requested)
        {
            CaptureManualStatus = V2ShellText.Get("V2.Shell.Announce.CaptureUnavailable");
            return;
        }

        if (!TarkovCompanion.App.Services.V2.Capture.CaptureIntentSupport.IsSupported(SelectedCaptureIntent))
        {
            CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureIntentNotSupported(IntentLabel(SelectedCaptureIntent));
            return;
        }

        if (HasActiveManualImageBatch)
        {
            CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureCancelBatchFirst;
            return;
        }

        var batchId = $"manual-{Guid.NewGuid():N}";
        _activeManualBatchId = batchId;
        _captureBatchItems = [.. items.Select((item, index) => new V2ManualImageBatchItemViewModel(
            item.Id,
            string.IsNullOrWhiteSpace(item.Label) ? TarkovCompanion.App.Localization.ShellText.CapturePicture(index + 1) : item.Label,
            TarkovCompanion.App.Localization.ShellText.CaptureRowQueued,
            false))];
        CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureQueuedPictures(items.Count);
        OnPropertyChanged(nameof(CaptureBatchItems));
        OnPropertyChanged(nameof(HasCaptureBatchItems));
        OnPropertyChanged(nameof(HasActiveManualImageBatch));
        requested(this, new(batchId, origin, items, SelectedCaptureIntent));
    }

    /// <summary>Updates one visible row as intake and recognition advance.</summary>
    public void ReportManualImageBatchItem(
        string batchId,
        string itemId,
        string status,
        bool isTerminal,
        CaptureCorrelationId? correlationId = null)
    {
        void Apply()
        {
            if (!string.Equals(_activeManualBatchId, batchId, StringComparison.Ordinal)
                && !CaptureBatchItems.Any(item => item.Id == itemId))
            {
                return;
            }

            _captureBatchItems = [.. CaptureBatchItems.Select(item => item.Id == itemId
                ? item with
                {
                    Status = status,
                    IsTerminal = isTerminal,
                    CorrelationId = correlationId ?? item.CorrelationId,
                }
                : item)];
            if (_captureBatchItems.All(item => item.IsTerminal))
            {
                _activeManualBatchId = null;
            }

            OnPropertyChanged(nameof(CaptureBatchItems));
            OnPropertyChanged(nameof(HasCaptureBatchItems));
            OnPropertyChanged(nameof(HasActiveManualImageBatch));
        }

        Dispatch(Apply);
    }

    public void ReportManualImageBatch(string status) => ReportManualImage(status);

    /// <summary>Render-preview seam for the visible state while three files wait in intake.</summary>
    internal void ShowManualImageBatchPreview(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _activeManualBatchId = "manual-render-preview";
        _captureBatchItems = [.. labels.Select((label, index) => new V2ManualImageBatchItemViewModel(
            $"preview-{index + 1}",
            label,
            TarkovCompanion.App.Localization.ShellText.CaptureRowQueued,
            false))];
        CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureQueuedPictures(labels.Count);
        OnPropertyChanged(nameof(CaptureBatchItems));
        OnPropertyChanged(nameof(HasCaptureBatchItems));
        OnPropertyChanged(nameof(HasActiveManualImageBatch));
    }

    /// <summary>What became of the last picture, in one short line under the button.</summary>
    public void ReportManualImage(string status)
    {
        void Apply() => CaptureManualStatus = status ?? string.Empty;
        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    private async Task PickCaptureImageAsync()
    {
        if (CaptureImagePicker is not { } pick)
        {
            CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureNoFilePicker;
            return;
        }

        var paths = await pick().ConfigureAwait(true);
        if (paths.Count == 1)
        {
            SubmitManualImage(V2ManualImageOrigin.Picker, paths[0], null);
        }
        else if (paths.Count > 1)
        {
            SubmitManualImages(
                V2ManualImageOrigin.Picker,
                [.. paths.Select(path => new V2ManualImageItem(
                    Guid.NewGuid().ToString("N"),
                    Path.GetFileName(path),
                    path,
                    null))]);
        }
    }

    private void CancelManualImageBatch()
    {
        if (_activeManualBatchId is not { } batchId)
        {
            return;
        }

        _captureBatchItems = [.. CaptureBatchItems.Select(item => item.IsTerminal
            ? item
            : item with { Status = TarkovCompanion.App.Localization.ShellText.CaptureRowCancelling })];
        CaptureManualStatus = TarkovCompanion.App.Localization.ShellText.CaptureCancellingRemaining;
        OnPropertyChanged(nameof(CaptureBatchItems));
        ManualImageBatchCancelRequested?.Invoke(this, batchId);
    }

    private void Dispatch(Action apply)
    {
        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            apply();
        }
        else
        {
            _dispatcherContext.Post(_ => apply(), null);
        }
    }

    /// <summary>Shows a photographed flea screen in Intel &gt; Flea and goes there.</summary>
    public void ShowFleaScan(TarkovCompanion.App.ViewModels.V2.Intel.FleaScanViewModel scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        void Apply()
        {
            FleaWorkspace?.ShowScan(scan);
            GoTo(V2Routes.Flea);
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    private ScanSourceViewModel? _captureReviewSource;

    /// <summary>#287: the retention chip and Read as… for the reviewed capture, or null.</summary>
    public ScanSourceViewModel? CaptureReviewSource
    {
        get => _captureReviewSource;
        private set
        {
            if (SetProperty(ref _captureReviewSource, value))
            {
                OnPropertyChanged(nameof(HasCaptureReviewSource));
            }
        }
    }

    public bool HasCaptureReviewSource => CaptureReviewSource is not null;

    /// <summary>Sets what the review says about its frame. The capture bridge owns it.</summary>
    public void ShowCaptureReviewSource(ScanSourceViewModel? source)
    {
        void Apply() => CaptureReviewSource = source;
        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    /// <summary>#287: a frame read again as the stash lands on the Stash page, so go there.</summary>
    public void ShowStashScan()
    {
        void Apply()
        {
            if (HasOpenDialog)
            {
                CloseDialog(restoreInvoker: false);
            }

            GoTo(V2Routes.Stash);
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    /// <summary>
    /// Opens the capture panel on its review when no other dialog is up: a result with no page of
    /// its own (#287, a screen nothing reads yet) must still be seen.
    /// </summary>
    public void OpenCaptureForReview()
    {
        void Apply()
        {
            if (!HasOpenDialog)
            {
                CaptureCommand.Execute(null);
            }
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    /// <summary>Closes the capture panel once a reviewed result has been opened on its own page.</summary>
    public void CloseCaptureAfterReview()
    {
        void Apply()
        {
            if (IsCaptureOpen)
            {
                CloseDialog(restoreInvoker: false);
            }
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    /// <summary>Called once from the constructor.</summary>
    private void InitializeManualCapture() =>
        PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(CaptureReviewSummary))
            {
                OnPropertyChanged(nameof(CaptureReviewCandidates));
                OnPropertyChanged(nameof(HasCaptureReviewCandidates));
            }
        };
}
