using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
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
    private string _captureManualStatus = string.Empty;
    private V2CaptureReview? _candidatesBuiltFor;
    private IReadOnlyList<V2CaptureCandidateViewModel> _captureReviewCandidates = [];

    /// <summary>Raised when the player pastes, drops or picks a picture. The capture bridge submits it.</summary>
    public event EventHandler<V2ManualImage>? ManualImageRequested;

    /// <summary>Raised when the player says a capture was a different candidate.</summary>
    public event EventHandler<V2CaptureCandidate>? CaptureCandidateChosen;

    /// <summary>Opens the platform's file picker. Set by the view, which owns the window.</summary>
    public Func<Task<string?>>? CaptureImagePicker { get; set; }

    public string CaptureManualHeading => "Or use a picture you already have";

    public string CaptureManualHint => "Paste with Ctrl+V, drop a file on the window, or choose one.";

    public string CapturePickLabel => "Choose a picture";

    public string CaptureCandidatesHeading => "Not that? Pick what it was";

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
            CaptureManualStatus = $"{IntentLabel(SelectedCaptureIntent)} is {TarkovCompanion.App.Services.V2.Capture.CaptureIntentSupport.NotSupportedYet}";
            return;
        }

        requested(this, new(origin, filePath, pixels, SelectedCaptureIntent));
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
            CaptureManualStatus = "No file picker is available here";
            return;
        }

        if (await pick().ConfigureAwait(true) is { } path)
        {
            SubmitManualImage(V2ManualImageOrigin.Picker, path, null);
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
