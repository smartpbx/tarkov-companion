using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Decides how closely the screenshot folder is worth watching, from what is already known.
/// </summary>
/// <remarks>
/// Two facts, both already in the runtime snapshot, so this asks nothing of anybody and can be
/// read on every poll: a raid is running, and the group is sharing. Together they mean somebody
/// else's map is waiting on the next screenshot, which is the only case that justifies looking
/// at a folder four times a second.
///
/// Outside those two the pace stays where it was. A player in the menu, in the hideout, or in a
/// raid on their own is not making anybody wait: their own map redraws from the same screenshot
/// either way, and a second is not long to wait for a marker only they will see.
///
/// The raid is established from the game's log, which happens at the loading screen — well
/// before anybody can take a screenshot in it — so the pace is already up by the time the first
/// one is taken.
/// </remarks>
public sealed class ScreenshotWatchPacer(IRuntimeStateStore stateStore) : IScreenshotWatchPacer
{
    public ScreenshotWatchPace Current
    {
        get
        {
            var snapshot = stateStore.Current;
            return snapshot.Raid.State == RaidLifecycleState.InRaid && snapshot.Group.IsSharing
                ? ScreenshotWatchPace.Attentive
                : ScreenshotWatchPace.Idle;
        }
    }
}
