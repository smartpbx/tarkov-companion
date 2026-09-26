using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>[#712 T4, 2-3] The recap's "Hand in: Golden Swag to Skier" line.</summary>
/// <remarks>
/// Read from the player's own board when the recap is built, the same way Plan's hand-in list is
/// (<see cref="HandInReminders"/>): the raid just ended is when a quest has most often become ready.
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private readonly IQuestReadService? _questRead;

    private async Task<RaidRecapLineViewModel?> HandInRecapLineAsync(CancellationToken cancellationToken)
    {
        if (_questRead is null || _profileService is null)
        {
            return null;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var board = await _questRead
                .GetQuestBoardAsync(new(profile.Id, profile.GameMode, profile.ProfileGeneration), cancellationToken)
                .ConfigureAwait(true);
            var quests = HandInReminders.Find(board.Tasks, profile.OwnedItemCounts)
                .Select(reminder => PlanText.HandInShort(reminder.TaskName, reminder.TraderLabel))
                .ToArray();
            return quests.Length == 0 ? null : new("Clipboard", DebriefText.RecapHandIn(Shortlist(quests)), string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("debrief", "read the quests ready to hand in", exception.Message);
            return null;
        }
    }
}
