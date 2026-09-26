using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.FormatGuards;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#712 0-3] Setup › Updates &amp; Diagnostics: whether the game's logs and screenshot names are
/// still in shapes this build reads.
/// </summary>
/// <remarks>
/// For everybody, not developer mode: after a game update this row is the one place that says
/// "raid detection may be wrong" rather than letting the maps and quests go quiet.
/// </remarks>
public sealed class FormatHealthReadinessViewModel : BindableViewModel
{
    private readonly FormatHealthMonitor _monitor;
    private readonly Action<Action> _post;
    private string _line = string.Empty;
    private bool _isDegraded;

    public FormatHealthReadinessViewModel(FormatHealthMonitor monitor, Action<Action>? post)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _post = post ?? (action => action());
        _monitor.Changed += (_, _) => _post(Refresh);
        Refresh();
    }

    public string Line
    {
        get => _line;
        private set => SetProperty(ref _line, value);
    }

    public bool IsDegraded
    {
        get => _isDegraded;
        private set => SetProperty(ref _isDegraded, value);
    }

    public void Refresh()
    {
        var report = _monitor.Current;
        Line = SetupText.FormatHealthRow(report);
        IsDegraded = report.IsDegraded;
    }
}
