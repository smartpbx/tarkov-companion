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
        _capability = SelfTestCapability.Waiting(id, title, "Not tested yet.");
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
        _ => "pending",
    };

    public bool IsPass => Outcome == SelfTestOutcome.Pass;

    public bool IsFail => Outcome == SelfTestOutcome.Fail;

    public bool IsUnknown => Outcome == SelfTestOutcome.Unknown;

    public bool IsRunning => Outcome == SelfTestOutcome.Running;

    /// <summary>How long it took, when that is worth saying. A read off disk is not.</summary>
    /// <remarks>
    /// Under a tenth of a second is "instant", and seven rows each announcing "0.0 s" is noise
    /// over the verdict beside it. The exact figure is still in the copied text.
    /// </remarks>
    public string Took => _capability.Took < TimeSpan.FromMilliseconds(100)
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{_capability.Took.TotalSeconds:0.0} s");

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
    private SelfTestSummary _summary = SelfTestSummary.Empty;
    private string _status = "Press Run self-test to check every part of this installation.";
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

    public string Heading => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestHeading");

    public string Intro => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestIntro");

    public string RunLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestRun");

    public string StopLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestStop");

    public string CopyLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestCopy");

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
            }
        }
    }

    public bool CanCopy => !IsRunning && _summary.Capabilities.Count > 0;

    /// <summary>
    /// The one thing the self-test asks the player to do.
    /// </summary>
    /// <remarks>
    /// Every other capability can be read off disk. A position only exists once somebody presses
    /// the game's screenshot key, so the panel says so while it is waiting rather than reporting
    /// a failure the player could have prevented.
    /// </remarks>
    public bool AsksForScreenshot => IsRunning &&
        Rows.Any(row => row.Id == SelfTestProbes.ScreenshotsId && row.Outcome == SelfTestOutcome.Running);

    public string ScreenshotPrompt => "Take a screenshot in the game now, so this can time one end to end.";

    public SelfTestSummary Summary => _summary;

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
        Status = "Testing every capability…";
        foreach (var row in Rows)
        {
            row.Apply(SelfTestCapability.Running(row.Id, row.Title, "Testing…"));
        }

        OnPropertyChanged(nameof(AsksForScreenshot));
        try
        {
            var session = new SelfTestSession(_readings(), _clock, CultureInfo.CurrentCulture);
            var summary = await session.RunAsync(Report, cancellation.Token).ConfigureAwait(true);
            _summary = summary;
            _journal.Record(summary);
            Status = summary.Headline(CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"The self-test itself failed: {exception.Message}";
        }
        finally
        {
            IsRunning = false;
            _running = null;
            cancellation.Dispose();
            OnPropertyChanged(nameof(AsksForScreenshot));
            OnPropertyChanged(nameof(CanCopy));
        }
    }

    public void Stop()
    {
        _running?.Cancel();
        Status = "Stopped.";
    }

    public async Task CopyAsync()
    {
        if (_summary.Capabilities.Count == 0)
        {
            CopyStatus = "There is nothing to copy yet.";
            return;
        }

        try
        {
            var text = _summary.ToText(CultureInfo.CurrentCulture);
            await Clipboard(text).ConfigureAwait(true);
            CopyStatus = string.Create(CultureInfo.CurrentCulture, $"Copied · {text.Length:N0} characters.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CopyStatus = $"Could not copy: {exception.Message}";
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
