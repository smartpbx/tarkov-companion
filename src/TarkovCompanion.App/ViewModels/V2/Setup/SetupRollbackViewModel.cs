using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>Where "Go back to the previous version" is.</summary>
public enum RollbackStage
{
    /// <summary>Run from a folder: nothing to go back from.</summary>
    Unavailable,

    /// <summary>The button, waiting.</summary>
    Idle,

    /// <summary>Reading the feed and the kept copy.</summary>
    Looking,

    /// <summary>Nothing older to go back to; says why.</summary>
    Nothing,

    /// <summary>Says what will happen and waits for a yes.</summary>
    Confirming,

    /// <summary>Fetching and checking the older build.</summary>
    Downloading,

    /// <summary>Handed to the updater; the application is closing.</summary>
    Applying,

    /// <summary>The fetch or the hand-over failed; nothing installed was touched.</summary>
    Failed,
}

/// <summary>
/// Setup › Updates: go back to the previous build, stay there, and where the running build came from.
/// </summary>
/// <remarks>
/// #292. Two presses, never one: the first says exactly what is about to happen (which build,
/// from where, that the window closes, that updates pause), the second does it. A rollback
/// that started on a single stray click would close the companion mid-session.
/// </remarks>
public sealed class SetupRollbackViewModel : BindableViewModel
{
    /// <summary>For the render tool only: a rollback to show on a machine with no installation.</summary>
    internal static IUpdateRollback? RenderDemo { get; set; }

    /// <summary>For the render tool only: open straight at the confirmation.</summary>
    internal static bool RenderConfirming { get; set; }

    private readonly IUpdateRollback? _rollback;
    private readonly Func<Task>? _afterResume;
    private RollbackStage _stage;
    private RollbackOffer? _offer;
    private string _status = string.Empty;
    private int _percent;
    private IReadOnlyList<UpdateProvenanceRow> _provenance = [];
    private UpdatePin? _pin;

    public SetupRollbackViewModel(IUpdateRollback? rollback, Func<Task>? afterResume = null)
    {
        _rollback = RenderDemo ?? rollback;
        _afterResume = afterResume;
        _stage = _rollback is { IsInstalled: true } ? RollbackStage.Idle : RollbackStage.Unavailable;
        StartCommand = new AsyncDelegateCommand(StartAsync);
        ConfirmCommand = new AsyncDelegateCommand(ConfirmAsync);
        CancelCommand = new DelegateCommand(Cancel);
        ResumeUpdatesCommand = new AsyncDelegateCommand(ResumeUpdatesAsync);
        Refresh();
        if (RenderDemo is not null && RenderConfirming)
        {
            StartAsync().GetAwaiter().GetResult();
        }
    }

    public AsyncDelegateCommand StartCommand { get; }

    public AsyncDelegateCommand ConfirmCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public AsyncDelegateCommand ResumeUpdatesCommand { get; }

    public RollbackStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(IsConfirming));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(ShowsInstallerNote));
            }
        }
    }

    /// <summary>Whether the button can be pressed: installed, and nothing under way.</summary>
    public bool CanStart => Stage is RollbackStage.Idle or RollbackStage.Nothing or RollbackStage.Failed;

    public bool IsConfirming => Stage == RollbackStage.Confirming;

    public bool IsDownloading => Stage == RollbackStage.Downloading;

    /// <summary>The installer route, for a build that cannot go back by itself.</summary>
    public bool ShowsInstallerNote => Stage is RollbackStage.Unavailable or RollbackStage.Nothing;

    public string StartLabel => "Go back to the previous version";

    public string CancelLabel => "Cancel";

    public string ResumeLabel => "Resume updates";

    public string ProvenanceHeading => "This build";

    public string GoingBackHeading => TarkovCompanion.App.Services.V2.Shell.V2ShellText.Get("V2.Setup.Updates.GoingBackHeading");

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

    public bool HasStatus => !string.IsNullOrEmpty(Status) && !IsConfirming;

    public int Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    /// <summary>The build that would be gone back to, while the confirmation is up.</summary>
    public RollbackOffer? Offer
    {
        get => _offer;
        private set
        {
            if (SetProperty(ref _offer, value))
            {
                OnPropertyChanged(nameof(ConfirmHeading));
                OnPropertyChanged(nameof(ConfirmSteps));
                OnPropertyChanged(nameof(ConfirmLabel));
            }
        }
    }

    public string ConfirmHeading => Offer is { Previous: { } target }
        ? $"Go back from {Offer.Installed} to {target.Version}?"
        : string.Empty;

    /// <summary>What happens, in order, one short line each.</summary>
    public IReadOnlyList<string> ConfirmSteps => Offer is { Previous: { } target } offer
        ?
        [
            target.Source == RollbackSource.Feed
                ? $"Downloads {target.Version} from the update feed and checks its SHA-256."
                : $"Uses the copy of {target.Version} kept on this PC and checks its SHA-256.",
            $"Closes the companion, installs {target.Version}, and reopens it.",
            "Your data and settings stay.",
            $"Stays on {target.Version} until a build newer than {UpdateRollbackRules.PinFor(target.Version, offer.Installed, offer.LatestInFeed, default).HoldThrough} is published.",
        ]
        : [];

    public string ConfirmLabel => Offer is { Previous: { } target } ? $"Go back to {target.Version}" : string.Empty;

    public IReadOnlyList<UpdateProvenanceRow> Provenance
    {
        get => _provenance;
        private set => SetProperty(ref _provenance, value);
    }

    public UpdatePin? Pin
    {
        get => _pin;
        private set
        {
            if (SetProperty(ref _pin, value))
            {
                OnPropertyChanged(nameof(HasPin));
                OnPropertyChanged(nameof(PinText));
            }
        }
    }

    public bool HasPin => Pin is not null;

    public string PinText => Pin is { } pin ? $"Staying on {pin.Version} until a build newer than {pin.HoldThrough}" : string.Empty;

    /// <summary>First press: find the previous build and ask.</summary>
    public async Task StartAsync()
    {
        if (_rollback is null || !CanStart)
        {
            return;
        }

        Stage = RollbackStage.Looking;
        Status = "Looking for the previous version…";
        try
        {
            var offer = await _rollback.FindPreviousAsync(CancellationToken.None).ConfigureAwait(true);
            Provenance = _rollback.Provenance();
            if (offer.Previous is null)
            {
                Offer = null;
                Status = offer.Reason ?? "No older build to go back to";
                Stage = RollbackStage.Nothing;
                return;
            }

            Offer = offer;
            Status = string.Empty;
            Stage = RollbackStage.Confirming;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status = $"Could not look for the previous version · {exception.Message}";
            Stage = RollbackStage.Failed;
        }
    }

    /// <summary>Second press: fetch, check, and hand over to the updater.</summary>
    public async Task ConfirmAsync()
    {
        if (_rollback is null || Stage != RollbackStage.Confirming || Offer is not { Previous: { } target } offer)
        {
            return;
        }

        Stage = RollbackStage.Downloading;
        Percent = 0;
        Status = $"Fetching {target.Version}…";
        var progress = new Progress<int>(percent => Percent = percent);
        var fetched = await _rollback
            .DownloadPreviousAsync(offer, ((IProgress<int>)progress).Report, CancellationToken.None)
            .ConfigureAwait(true);
        if (!fetched.CanApply)
        {
            Status = fetched.Status;
            Stage = RollbackStage.Failed;
            return;
        }

        Stage = RollbackStage.Applying;
        Status = $"Installing {target.Version} · it will close and reopen";
        try
        {
            _rollback.ApplyAndRestart();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = $"Going back could not be started: {exception.Message}";
            Stage = RollbackStage.Failed;
        }
    }

    public void Cancel()
    {
        if (Stage != RollbackStage.Confirming)
        {
            return;
        }

        Offer = null;
        Status = string.Empty;
        Stage = RollbackStage.Idle;
    }

    private async Task ResumeUpdatesAsync()
    {
        if (_rollback is null)
        {
            return;
        }

        _rollback.ResumeUpdates();
        Refresh();
        if (_afterResume is not null)
        {
            await _afterResume().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the pin and the provenance again; after a check, or on showing the page.</summary>
    public void Refresh()
    {
        if (_rollback is null)
        {
            return;
        }

        Pin = _rollback.Pin;
        Provenance = _rollback.Provenance();
    }
}
