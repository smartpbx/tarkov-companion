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
/// </remarks>
public sealed class SetupReportViewModel : BindableViewModel
{
    private readonly Func<string?> _build;
    private readonly Func<string, CancellationToken, Task<string>> _send;
    private string? _reviewed;
    private string _status = string.Empty;
    private bool _isSending;

    /// <param name="build">Produces the report text; null when there is nothing to describe yet.</param>
    /// <param name="send">Sends exactly the text it is given, and returns a sentence saying what happened.</param>
    public SetupReportViewModel(Func<string?> build, Func<string, CancellationToken, Task<string>> send)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        PreviewCommand = new DelegateCommand(Preview);
        SendCommand = new AsyncDelegateCommand(SendAsync);
        DiscardCommand = new DelegateCommand(Discard);
    }

    public ICommand PreviewCommand { get; }

    public ICommand SendCommand { get; }

    public ICommand DiscardCommand { get; }

    public string ReviewLabel => V2ShellText.Get("V2.Setup.Report.Review");
    public string SendLabel => V2ShellText.Get("V2.Setup.Report.Send");
    public string DiscardLabel => V2ShellText.Get("V2.Setup.Report.Discard");
    public string Heading => V2ShellText.Get("V2.Setup.Report.Heading");
    public string Note => V2ShellText.Get("V2.Setup.Report.Note");

    /// <summary>The report as it would be sent; empty until the player asks to see it.</summary>
    public string ReportText => _reviewed ?? string.Empty;

    public bool HasPreview => _reviewed is not null;

    public string Size => _reviewed is null
        ? string.Empty
        : V2ShellText.Format("V2.Setup.Report.Size", CultureInfo.CurrentCulture, _reviewed.Length);

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
        Status = _reviewed is null ? V2ShellText.Get("V2.Setup.Report.Nothing") : string.Empty;
        Changed();
    }

    private async Task SendAsync()
    {
        if (_reviewed is not { } report || _isSending)
        {
            return;
        }

        _isSending = true;
        Status = V2ShellText.Get("V2.Setup.Report.Sending");
        try
        {
            Status = await _send(report, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = V2ShellText.Format("V2.Setup.Report.Failed", CultureInfo.CurrentCulture, exception.Message);
        }
        finally
        {
            _isSending = false;
        }

        // Sent, or failed: either way the next report is a new preview, so a second Send is a second consent.
        _reviewed = null;
        Changed();
    }

    private void Discard()
    {
        _reviewed = null;
        Status = string.Empty;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(ReportText));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(Size));
    }
}
