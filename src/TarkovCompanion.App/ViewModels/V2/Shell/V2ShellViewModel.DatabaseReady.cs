using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public sealed partial class V2ShellViewModel
{
    private bool _databaseWasReady;

    /// <summary>
    /// #871: a first launch that opens on Debrief or Stash read them before startup had migrated
    /// the new database ("no such table: raids") and never read them again, so a fresh install's
    /// Debrief said it could not read the raid history. Plan and Hideout reload when game data
    /// lands; these two do not depend on game data, only on the database, so they reload once
    /// when it becomes ready.
    /// </summary>
    private void ReloadWhenDatabaseBecomesReady(bool databaseReady)
    {
        if (databaseReady == _databaseWasReady)
        {
            return;
        }

        _databaseWasReady = databaseReady;
        var route = Router.Current.Location.Route;
        if (databaseReady && (route == V2Routes.Debrief || route == V2Routes.Stash))
        {
            LoadCurrentWorkspace();
        }
    }
}
