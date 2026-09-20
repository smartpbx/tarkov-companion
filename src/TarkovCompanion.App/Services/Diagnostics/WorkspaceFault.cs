namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Records a failure a workspace absorbed, somewhere a player's log will actually show it.
/// </summary>
/// <remarks>
/// A workspace that cannot load says so on the page in plain words and keeps the raw exception
/// out of the copy, which is right. Where it went was <see cref="System.Diagnostics.Trace"/>,
/// which is wrong: nothing listens to Trace in an installed build —
/// <see cref="InterfaceWarningLog"/> attaches a listener only when a verification tool names a
/// file in the environment — so in a player's run every one of those warnings was formatted and
/// then discarded.
///
/// That is the other half of "panes not rendering, and no exception in the log", reported on
/// 2026-09-19. The pane had caught its exception and written it to nobody, so the only evidence
/// left was the placeholder sentence on screen, and a placeholder cannot say whether the cause
/// was a migration that had not run, a catalog that had not synced, or a defect.
///
/// So this writes through <see cref="CrashLog"/>, the file the player is asked to send, and
/// leaves a breadcrumb as well, because a workspace fault is often the last thing that happens
/// before something worse.
/// </remarks>
public static class WorkspaceFault
{
    /// <summary>Records a workspace failure with its full exception detail.</summary>
    /// <param name="surface">The workspace or panel, in the words the shell uses: "plan", "hideout".</param>
    /// <param name="what">What was being attempted, as a phrase: "refresh", "name map reserve".</param>
    public static void Record(string surface, string what, Exception exception) =>
        Record(surface, what, exception.ToString());

    /// <summary>Records a workspace failure that is already reduced to a message.</summary>
    /// <remarks>
    /// Used where the original site deliberately logged <c>exception.Message</c> rather than the
    /// whole exception, because the failure is one row or one label rather than the panel.
    /// </remarks>
    public static void Record(string surface, string what, string detail)
    {
        CrashLog.Write($"workspace-fault/{surface}", $"{what}: {detail}");
        CrashBreadcrumbs.Drop("workspace-fault", $"{surface} {what}");
    }
}
