using System.ComponentModel;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// The top bar's raid clock is the Raid page's raid clock.
/// </summary>
/// <remarks>
/// The shell used to work the clock out for itself from the raid's start, which made it a second
/// clock: it counted up ("14:03 elapsed") while the Raid page beside it counted down from the
/// map's length ("0:20:56"), and the shell has no way to know that length. It now shows the string
/// the Raid page already holds (<see cref="RaidPageViewModel.Clock"/>) and repeats it the moment it
/// changes, so the two cannot disagree even for the second between two header ticks. A shell built
/// without the legacy view model (a test) keeps its own arithmetic.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private void WireRaidClock()
    {
        if (Legacy is not null)
        {
            Legacy.Raid.PropertyChanged += RaidClockChanged;
        }
    }

    private void UnwireRaidClock()
    {
        if (Legacy is not null)
        {
            Legacy.Raid.PropertyChanged -= RaidClockChanged;
        }
    }

    private void RaidClockChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_disposed || eventArgs.PropertyName != nameof(RaidPageViewModel.Clock))
        {
            return;
        }

        OnPropertyChanged(nameof(RaidClockLabel));
        OnPropertyChanged(nameof(RaidContextLabel));
    }

    /// <summary>The shared clock text while a raid is running and the Raid page has one, otherwise null.</summary>
    private string? SharedRaidClock(RaidSnapshot raid) =>
        raid.State == RaidLifecycleState.InRaid && Legacy?.Raid.Clock is { Length: > 0 } clock ? clock : null;
}
