using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#712 2-4] The Now panel's read of the player's own history: their pace over every map's trails,
/// and the exits they used on this map and side. Own recorded raids only.
/// </summary>
public sealed partial class RaidCockpitViewModel
{
    /// <summary>
    /// The raid history the Now panel reads the player's pace and exits from. Its own property rather
    /// than the constructor's <c>raidHistory</c>, which the composition does not pass (and which would
    /// also start recording route choices).
    /// </summary>
    public IRaidHistoryService? PersonalHistorySource { get; init; }

    private IRaidHistoryService? NowHistory => PersonalHistorySource ?? _raidHistory;

    private async Task<NowPersonal> LoadNowPersonalAsync(string mapId, SituationSide side, CancellationToken cancellationToken)
    {
        if (NowHistory is not { } history)
        {
            return NowPersonal.None;
        }

        var reader = new PersonalHistory(history);
        var pace = await reader.PaceAsync(cancellationToken).ConfigureAwait(false);
        var uses = await reader.ExitUsesAsync(
            mapId,
            side == SituationSide.Unknown ? null : side.ToString(),
            cancellationToken).ConfigureAwait(false);
        return new NowPersonal(pace, uses);
    }
}
