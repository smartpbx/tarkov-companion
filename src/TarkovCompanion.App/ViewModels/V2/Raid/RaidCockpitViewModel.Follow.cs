using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#992] Follow on, paused by a drag, and resumed by a tap or the next raid.</summary>
public sealed partial class RaidCockpitViewModel
{
    private Guid? _followRaidId;

    /// <summary>Follow is on for this map and the player moved the map since.</summary>
    public bool IsFollowPaused => _map.IsFollowPaused;

    /// <summary>"Follow", or "Follow paused" while a drag has paused it: the button says which.</summary>
    public string FollowButtonText => _map.IsFollowPaused ? RaidText.FollowPaused : RaidText.Follow;

    public string FollowButtonTip => _map.IsFollowPaused ? RaidText.FollowPausedTip : RaidText.FollowTip;

    /// <summary>Stops following for now without changing the saved choice (the page gallery's plan clicks).</summary>
    internal void PauseFollow() => _map.PauseFollow();

    /// <summary>
    /// A new raid follows again: a drag in the last raid paused following for that raid only.
    /// </summary>
    private void ResumeFollowForNewRaid(RaidSnapshot? raid)
    {
        if (raid?.RaidId is not { } raidId || raidId == _followRaidId)
        {
            return;
        }

        _followRaidId = raidId;
        _map.ResumeFollow();
    }
}
