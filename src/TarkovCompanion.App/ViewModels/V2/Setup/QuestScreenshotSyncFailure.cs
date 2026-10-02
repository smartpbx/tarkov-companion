using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>Turns a failed screenshot sync into words a player can act on.</summary>
/// <remarks>
/// The status line used to print the exception message, so a refused write showed the
/// store's own rule text ("Stage 2 progress accepts manual commands...") to the player (#989).
/// The message still goes to the log a player is asked to send; the page gets a plain reason.
/// </remarks>
internal static class QuestScreenshotSyncFailure
{
    public static string Reason(Exception exception, string what)
    {
        WorkspaceFault.Record("quest-sync", what, exception);
        return exception switch
        {
            QuestScreenshotCatalogNotReadyException => SetupText.QuestSyncFailureNoQuestData,
            QuestCatalogEntryUnknownException => SetupText.QuestSyncFailureUnknownQuest,
            QuestScreenshotTextUnavailableException => SetupText.QuestSyncFailureNoTextReading,
            _ => SetupText.QuestSyncFailureOther,
        };
    }
}
