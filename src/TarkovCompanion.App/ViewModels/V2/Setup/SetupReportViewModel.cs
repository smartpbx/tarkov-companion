using TarkovCompanion.App.Localization;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// Setup › Diagnostics › Report a problem, in two steps: read exactly what would be sent, then send that (#292, #309).
/// </summary>
/// <remarks>
/// "Report a problem" used to build the report and send it in one press, so the player agreed to text they had
/// never seen. This shows the whole report first, and Send sends <em>that text</em>: it is built once, at preview
/// time, and the same string is what goes out, so nothing can change between what was read and what was sent.
/// Pressing Send with nothing previewed does nothing, and the preview alone sends nothing. That is the separate,
/// per-purpose consent #309 asks for: agreeing to the report is agreeing to this text, and to nothing else.
///
/// #314 made the agreement an act of its own: the preview says where the text goes, and Send does nothing until
/// the box under it is ticked. The tick belongs to one preview; a new preview, a send or a discard clears it.
/// </remarks>
public sealed class SetupReportViewModel : BindableViewModel
{
    private readonly Func<string?> _build;
    private readonly Func<string, CancellationToken, Task<string>> _send;
    private string? _reviewed;
    private string _status = string.Empty;
    private bool _isSending;
    private bool _consented;
    private readonly Func<string>? _pending;

    /// <param name="build">Produces the report text; null when there is nothing to describe yet.</param>
    /// <param name="send">Sends exactly the text it is given, and returns a sentence saying what happened.</param>
    /// <param name="pending">Describes reports queued for a retry; empty when there are none.</param>
    public SetupReportViewModel(
        Func<string?> build,
        Func<string, CancellationToken, Task<string>> send,
        Func<string>? pending = null)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _pending = pending;
        PreviewCommand = new DelegateCommand(Preview);
        SendCommand = new AsyncDelegateCommand(SendAsync);
        DiscardCommand = new DelegateCommand(Discard);
    }

    public ICommand PreviewCommand { get; }

    public ICommand SendCommand { get; }

    public ICommand DiscardCommand { get; }

    public string ReviewLabel => SetupText.ReportReview;
    public string SendLabel => SetupText.ReportSend;
    public string DiscardLabel => SetupText.ReportDiscard;
    public string Heading => SetupText.ReportHeading;
    public string Note => SetupText.ReportNote;
    public string Destination => SetupText.ReportDestination;
    public string ConsentLabel => SetupText.ReportConsent;

    /// <summary>The player's agreement to send the report on screen; cleared whenever that report changes or goes.</summary>
    public bool Consented
    {
        get => _consented;
        set => SetProperty(ref _consented, value);
    }

    /// <summary>Reports waiting for the relay, e.g. "1 report queued · next try 14:05"; empty when none are.</summary>
    public string Pending => _pending?.Invoke() ?? string.Empty;

    public bool HasPending => Pending.Length > 0;

    /// <summary>The report as it would be sent; empty until the player asks to see it.</summary>
    public string ReportText => _reviewed ?? string.Empty;

    public bool HasPreview => _reviewed is not null;

    public string Size => _reviewed is null
        ? string.Empty
        : SetupText.ReportSize(_reviewed.Length);

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

    private void Preview()
    {
        // Built once and kept: this string is what Send sends, not something rebuilt at the moment of sending.
        _reviewed = _build();
        Consented = false;
        Status = _reviewed is null ? SetupText.ReportNothing : string.Empty;
        Changed();
    }

    private async Task SendAsync()
    {
        if (_reviewed is not { } report || _isSending)
        {
            return;
        }

        if (!Consented)
        {
            Status = SetupText.ReportNeedsConsent;
            return;
        }

        _isSending = true;
        Status = SetupText.ReportSending;
        try
        {
            Status = await _send(report, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = SetupText.ReportFailed(exception.Message);
        }
        finally
        {
            _isSending = false;
        }

        // Sent, or failed: either way the next report is a new preview, so a second Send is a second consent.
        _reviewed = null;
        Consented = false;
        Changed();
    }

    private void Discard()
    {
        _reviewed = null;
        Consented = false;
        Status = string.Empty;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(ReportText));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(Pending));
        OnPropertyChanged(nameof(HasPending));
    }

    /// <summary>For the outbox to call when a retry changes what is queued.</summary>
    public void RefreshPending()
    {
        OnPropertyChanged(nameof(Pending));
        OnPropertyChanged(nameof(HasPending));
    }
}
