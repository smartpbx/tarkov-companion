using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>One "Read as…" choice.</summary>
public sealed class ScanReadAsOptionViewModel(ScanIntent intent, ICommand command)
{
    public ScanIntent Intent { get; } = intent;

    public string Label { get; } = ScanReadAs.Label(intent);

    public string AutomationId { get; } = $"v2-scan-read-as-{intent.ToString().ToLowerInvariant()}";

    public ICommand Command { get; } = command;
}

/// <summary>
/// Where a scan result's pixels are, and "Read as…" for the same frame (#287).
/// </summary>
/// <remarks>
/// Shared by the Loot page and the capture review, so every result says the same thing about its
/// source. The choices exist only while <see cref="ScanFrameMemory"/> still holds the frame; after
/// that the menu says so instead of offering buttons that would fail.
/// </remarks>
public sealed class ScanSourceViewModel : BindableViewModel
{
    private readonly Func<ScanIntent, Task<ScanReadAsOutcome>>? _readAs;
    private ScanRetentionLabel _retention;
    private bool _isReadAsOpen;
    private bool _isReading;
    private string _status = string.Empty;

    public ScanSourceViewModel(
        CaptureSourceKind sourceKind,
        ScreenshotRetentionSettings? tidy,
        ScanIntent readAs,
        bool frameHeld,
        Func<ScanIntent, Task<ScanReadAsOutcome>>? reread,
        string? correction = null)
    {
        SourceKind = sourceKind;
        FrameHeld = frameHeld && reread is not null;
        _retention = ScanRetention.Describe(sourceKind, tidy, FrameHeld);
        ReadAs = readAs;
        _readAs = reread;
        Correction = correction ?? string.Empty;
        ReadAsOptions = FrameHeld
            ? [.. ScanReadAs.Intents
                .Where(intent => intent != readAs)
                .Select(intent => new ScanReadAsOptionViewModel(intent, new AsyncDelegateCommand(() => ReadAgainAsync(intent))))]
            : [];
        ToggleReadAsCommand = new DelegateCommand(() => IsReadAsOpen = !IsReadAsOpen);
    }

    public CaptureSourceKind SourceKind { get; }

    public bool FrameHeld { get; }

    /// <summary>What the frame was read as.</summary>
    public ScanIntent ReadAs { get; }

    public string RetentionLabel => _retention.Label;

    public string RetentionDetail => _retention.Detail;

    public string ReadAsLabel => TarkovCompanion.App.Localization.ShellText.ReadAsMenu;

    public string ReadAsHeading => TarkovCompanion.App.Localization.ShellText.ReadAsHeading(ScanReadAs.Label(ReadAs));

    public string ReadAsUnavailable => TarkovCompanion.App.Localization.ShellText.ReadAsImageReleased;

    public IReadOnlyList<ScanReadAsOptionViewModel> ReadAsOptions { get; }

    public bool CanReadAs => ReadAsOptions.Count > 0;

    public bool CannotReadAs => !CanReadAs;

    /// <summary>"Read as Stash by you · was Loot", when this result is a correction.</summary>
    public string Correction { get; }

    public bool HasCorrection => Correction.Length > 0;

    public ICommand ToggleReadAsCommand { get; }

    public bool IsReadAsOpen
    {
        get => _isReadAsOpen;
        set => SetProperty(ref _isReadAsOpen, value);
    }

    public bool IsReading
    {
        get => _isReading;
        private set => SetProperty(ref _isReading, value);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>The tidy setting arrives after the chip is built; the chip is redrawn with it.</summary>
    public void ApplyTidy(ScreenshotRetentionSettings? tidy)
    {
        _retention = ScanRetention.Describe(SourceKind, tidy, FrameHeld);
        OnPropertyChanged(nameof(RetentionLabel));
        OnPropertyChanged(nameof(RetentionDetail));
    }

    private async Task ReadAgainAsync(ScanIntent intent)
    {
        if (_readAs is null || IsReading)
        {
            return;
        }

        IsReading = true;
        try
        {
            var outcome = await _readAs(intent).ConfigureAwait(true);
            Status = outcome.Message;
            IsReadAsOpen = !outcome.Started;
        }
        finally
        {
            IsReading = false;
        }
    }
}
