using TarkovCompanion.App.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One capability's row in the self-test panel.</summary>
public sealed class SelfTestRowViewModel : BindableViewModel
{
    private SelfTestCapability _capability;

    public SelfTestRowViewModel(string id, string title)
    {
        _capability = SelfTestCapability.Waiting(id, title, SetupText.SelfTestNotTested);
        Facts = [];
    }

    public string Id => _capability.Id;

    public string Title => _capability.Title;

    public string AutomationId => $"v2-selftest-{Id}";

    public string Headline => _capability.Headline;

    public SelfTestOutcome Outcome => _capability.Outcome;

    /// <summary>The word beside the title. Never a bare tick: a tick is what this page replaces.</summary>
    public string Status => SelfTestSummary.Word(_capability.Outcome);

    /// <summary>The style class, so the row's colour follows its verdict.</summary>
    public string StatusClass => _capability.Outcome switch
    {
        SelfTestOutcome.Pass => "ready",
        SelfTestOutcome.Fail => "failed",
        SelfTestOutcome.Unknown => "unconfirmed",
        // [V2 rough package 43a] Waiting is not a verdict, so it borrows the neutral style rather
        // than any of the three that mean something was measured.
        SelfTestOutcome.Waiting => "pending",
        _ => "pending",
    };

    public bool IsPass => Outcome == SelfTestOutcome.Pass;

    public bool IsFail => Outcome == SelfTestOutcome.Fail;

    public bool IsUnknown => Outcome == SelfTestOutcome.Unknown;

    /// <summary>Open and waiting for the player — not a failure, and not finished.</summary>
    public bool IsWaiting => Outcome == SelfTestOutcome.Waiting;

    public bool IsRunning => Outcome == SelfTestOutcome.Running;

    /// <summary>How long it took, when that is worth saying. A read off disk is not.</summary>
    /// <remarks>
    /// Under a tenth of a second is "instant", and seven rows each announcing "0.0 s" is noise
    /// over the verdict beside it. The exact figure is still in the copied text.
    /// </remarks>
    public string Took => _capability.Took < TimeSpan.FromMilliseconds(100)
        ? string.Empty
        : SetupText.SelfTestTook(_capability.Took.TotalSeconds);

    public ObservableCollection<SelfTestFactViewModel> Facts { get; }

    public void Apply(SelfTestCapability capability)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        Facts.Clear();
        foreach (var fact in capability.Facts)
        {
            Facts.Add(new(fact));
        }

        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusClass));
        OnPropertyChanged(nameof(IsPass));
        OnPropertyChanged(nameof(IsFail));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Took));
    }
}

/// <summary>One measured fact, with the thing it was measured from underneath it.</summary>
public sealed class SelfTestFactViewModel(SelfTestFact fact)
{
    public string Text { get; } = fact.Text;

    public string Source { get; } = fact.Source;
}

/// <summary>
/// The button in Setup that proves the installation works, and what it found.
/// </summary>
/// <remarks>
/// Setup already showed readiness. Readiness is a set of conditions somebody decided were
/// enough; this presses each capability and reports what came back, which is a different
/// question and the one that kept costing hours — an empty refresh that named no endpoint, a
/// log folder that had stopped moving, screenshot times that came from OneDrive rather than
/// from the game.
///
/// Safe to press mid-raid. The session it drives is read-only and bounded, and Stop cancels it
/// at any point; nothing here writes to the game's folders or changes anything on the relay.
/// </remarks>
public sealed class SetupSelfTestViewModel : BindableViewModel
{
    private readonly Func<ISelfTestReadings> _readings;
    private readonly SelfTestJournal _journal;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _toUiThread;
    private CancellationTokenSource? _running;
    private Task? _settling;
    private SelfTestSummary _summary = SelfTestSummary.Empty;
    private string _status = SetupText.SelfTestPrompt;
    private string _copyStatus = string.Empty;
    private bool _isRunning;

    public SetupSelfTestViewModel(
        Func<ISelfTestReadings> readings,
        SelfTestJournal journal,
        TimeProvider? timeProvider = null,
        // The dispatcher, as a function, so this is testable without an Avalonia application.
        Action<Action>? toUiThread = null)
    {
        _readings = readings ?? throw new ArgumentNullException(nameof(readings));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = timeProvider ?? TimeProvider.System;
        _toUiThread = toUiThread ?? (action => action());
        Rows = [.. SelfTestSession.Capabilities.Select(capability => new SelfTestRowViewModel(capability.Id, capability.Title))];
        RunCommand = new AsyncDelegateCommand(RunAsync);
        StopCommand = new DelegateCommand(Stop);
        CopyCommand = new AsyncDelegateCommand(CopyAsync);
    }

    public ObservableCollection<SelfTestRowViewModel> Rows { get; }

    /// <summary>
    /// Every capability's verdict in one strip, above the detail.
    /// </summary>
    /// <remarks>
    /// Seven capabilities each carrying up to seven facts is longer than a 1920x1080 window, so
    /// the answer to "is it working" was below the fold. The strip is the same rows, so it
    /// cannot say something the detail under it does not.
    /// </remarks>
    public ObservableCollection<SelfTestRowViewModel> Strip => Rows;

    public string Heading => SetupText.DiagnosticsSelfTestHeading;

    public string Intro => SetupText.DiagnosticsSelfTestIntro;

    public string RunLabel => SetupText.DiagnosticsSelfTestRun;

    public string StopLabel => SetupText.DiagnosticsSelfTestStop;

    public string CopyLabel => SetupText.DiagnosticsSelfTestCopy;

    public ICommand RunCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand CopyCommand { get; }

    /// <summary>How the result reaches the clipboard, supplied by the shell.</summary>
    public Func<string, Task> Clipboard { get; set; } = _ => Task.CompletedTask;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string CopyStatus
    {
        get => _copyStatus;
        private set => SetProperty(ref _copyStatus, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanCopy));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool CanCopy => !IsRunning && _summary.Capabilities.Count > 0;

    /// <summary>
    /// Whether there is anything to stop: the run, or the screenshot wait that outlives it.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] The wait is harmless and gives up on its own after a few minutes,
    /// but a control that is still open with no visible way to close it is its own small
    /// annoyance.
    /// </remarks>
    public bool CanStop => IsRunning || AsksForScreenshot;

    /// <summary>
    /// The one thing the self-test asks the player to do.
    /// </summary>
    /// <remarks>
    /// Every other capability can be read off disk. A position only exists once somebody presses
    /// the game's screenshot key, so the panel says so while it is waiting rather than reporting
    /// a failure the player could have prevented.
    /// </remarks>
    /// <remarks>
    /// [V2 rough package 43a] No longer tied to the run. The run finishes; this one capability goes
    /// on waiting in the background for a few minutes and settles itself, so the prompt has to
    /// outlive <see cref="IsRunning"/> or it would vanish exactly when it became true.
    /// </remarks>
    public bool AsksForScreenshot =>
        Rows.Any(row => row.Id == SelfTestProbes.ScreenshotsId &&
            row.Outcome is SelfTestOutcome.Running or SelfTestOutcome.Waiting);

    public string ScreenshotPrompt =>
        SetupText.SelfTestScreenshotPrompt;

    public SelfTestSummary Summary => _summary;

    /// <summary>
    /// The screenshot probe's background wait, while one is open.
    /// </summary>
    /// <remarks>
    /// Internal so a test can await the settle rather than poll for it; nothing in the interface
    /// needs it, because the panel learns the answer through the same rows the run updates.
    /// </remarks>
    internal Task? Settling => _settling;

    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        IsRunning = true;
        CopyStatus = string.Empty;
        Status = SetupText.SelfTestTesting;
        foreach (var row in Rows)
        {
            row.Apply(SelfTestCapability.Running(row.Id, row.Title, SetupText.SelfTestRowTesting));
        }

        OnPropertyChanged(nameof(AsksForScreenshot));
        OnPropertyChanged(nameof(CanStop));
        try
        {
            var session = new SelfTestSession(_readings(), _clock, CultureInfo.CurrentCulture);
            var summary = await session.RunAsync(Report, cancellation.Token).ConfigureAwait(true);
            _summary = summary;
            _journal.Record(summary);
            Status = summary.Headline(CultureInfo.CurrentCulture);
            // The screenshot probe may still be open. It is not the run's business any more: the
            // other six are settled and copyable, and this resolves itself when a screenshot shows
            // up or quietly gives up saying nothing is wrong.
            if (session.Settling is { } settling)
            {
                _settling = SettleAsync(settling, cancellation);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = SetupText.SelfTestFailed(exception.Message);
        }
        finally
        {
            IsRunning = false;
            if (_settling is null)
            {
                _running = null;
                cancellation.Dispose();
            }

            // Otherwise the cancellation source stays alive and stays reachable from Stop, because
            // the screenshot probe is still using it. SettleAsync owns it from here.
            OnPropertyChanged(nameof(AsksForScreenshot));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanCopy));
        }
    }

    /// <summary>
    /// Folds the screenshot probe's late answer into the summary, once it settles.
    /// </summary>
    /// <remarks>
    /// Re-records the journal because the run recorded a summary that said "waiting", and a report
    /// somebody copies an hour later should carry what actually happened rather than the state it
    /// was in when the button was pressed.
    /// </remarks>
    private async Task SettleAsync(Task<SelfTestCapability> settling, CancellationTokenSource owner)
    {
        SelfTestCapability capability;
        try
        {
            capability = await settling.ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = SetupText.SelfTestScreenshotFailed(exception.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            // Held open past the run so Stop can still reach this wait; released once it is over.
            _settling = null;
            if (ReferenceEquals(_running, owner))
            {
                _running = null;
            }

            owner.Dispose();
        }

        _summary = _summary with
        {
            Capabilities = [.. _summary.Capabilities.Select(existing =>
                existing.Id == SelfTestProbes.ScreenshotsId ? capability : existing)],
        };
        _journal.Record(_summary);
        Status = _summary.Headline(CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(AsksForScreenshot));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanCopy));
    }

    public void Stop()
    {
        _running?.Cancel();
        Status = SetupText.SelfTestStopped;
    }

    public async Task CopyAsync()
    {
        if (_summary.Capabilities.Count == 0)
        {
            CopyStatus = SetupText.SelfTestNothingToCopy;
            return;
        }

        try
        {
            var text = _summary.ToText(CultureInfo.CurrentCulture);
            await Clipboard(text).ConfigureAwait(true);
            CopyStatus = SetupText.SelfTestCopied(text.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CopyStatus = SetupText.SelfTestCopyFailed(exception.Message);
        }
    }

    private void Report(SelfTestCapability capability) => _toUiThread(() =>
    {
        foreach (var row in Rows.Where(row => row.Id == capability.Id))
        {
            row.Apply(capability);
        }

        OnPropertyChanged(nameof(AsksForScreenshot));
    });
}
