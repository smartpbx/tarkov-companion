namespace TarkovCompanion.Application.Services.Workspaces;

/// <summary>
/// [#935] Runs a page's "read what is stored again" after Backup &amp; reset replaced the layout, on
/// the thread that owned the page when it subscribed.
/// </summary>
/// <remarks>
/// Every <see cref="PageState"/> owner read its fields once, in its constructor, and wrote them
/// back on its next filter pass. After Reset everything or an import the page kept showing the old
/// chips, and Debrief wrote its old filters straight back into <c>page.debrief</c>, so the reset
/// undid itself. The owner re-reads here instead. <see cref="IWorkspaceLayoutStore.Replaced"/> is
/// raised on whatever thread made the replacement, so the re-read is posted to the context captured
/// at subscription (the UI thread for a page built there) when it comes from another thread.
/// </remarks>
public static class WorkspaceLayoutReplaced
{
    public static void Reread(IWorkspaceLayoutStore? store, Action reread)
    {
        ArgumentNullException.ThrowIfNull(reread);
        if (store is null)
        {
            return;
        }

        var context = SynchronizationContext.Current;
        var thread = Environment.CurrentManagedThreadId;
        store.Replaced += (_, _) =>
        {
            // By thread, not by context: a UI dispatcher can install a new context object per
            // priority, and a deferred re-read lets a page's next pass write its old filters back first.
            if (context is not null && Environment.CurrentManagedThreadId != thread)
            {
                context.Post(_ => reread(), null);
                return;
            }

            reread();
        };
    }
}
