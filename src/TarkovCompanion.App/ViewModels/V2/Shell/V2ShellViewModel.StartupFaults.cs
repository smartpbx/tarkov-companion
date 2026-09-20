using System.ComponentModel;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// The shell's banner for pages that did not load at startup.
/// </summary>
/// <remarks>
/// <see cref="MainWindowViewModel.StartupFaults"/> has named the pages that failed since the ten
/// startup loads were made independent of each other, and until now only the problem report read
/// it: a player whose Hideout never filled in was told nothing, anywhere (#453). The banner names
/// the pages and offers Retry, which runs those loads again and nothing else.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private LoadFaultNoticeViewModel? _startupFaultNotice;

    /// <summary>Visible while any startup page is still unloaded.</summary>
    public LoadFaultNoticeViewModel StartupFaultNotice =>
        _startupFaultNotice ??= new(() => Legacy?.RetryStartupFaultsAsync() ?? Task.CompletedTask);

    private void WireStartupFaults()
    {
        if (Legacy is null)
        {
            return;
        }

        Legacy.PropertyChanged += StartupFaultsChanged;
        ApplyStartupFaults(Legacy.StartupFaults);
    }

    private void StartupFaultsChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (!_disposed && Legacy is not null && eventArgs.PropertyName == nameof(MainWindowViewModel.StartupFaults))
        {
            ApplyStartupFaults(Legacy.StartupFaults);
        }
    }

    private void ApplyStartupFaults(IReadOnlyList<string> faults)
    {
        if (faults.Count == 0)
        {
            StartupFaultNotice.Clear();
            return;
        }

        StartupFaultNotice.Show(
            DescribeStartupFaults(faults),
            faults.Count == 1
                ? "The rest of the app is working. Retry loads it again."
                : "The rest of the app is working. Retry loads them again.");
    }

    /// <summary>"Hideout and Map did not load", from the names startup uses for its pages.</summary>
    internal static string DescribeStartupFaults(IReadOnlyList<string> faults)
    {
        var names = faults
            .Select(fault => fault.Length == 0 ? fault : char.ToUpperInvariant(fault[0]) + fault[1..])
            .ToArray();
        var list = names.Length switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names[..^1])} and {names[^1]}",
        };
        return $"{list} did not load";
    }
}
