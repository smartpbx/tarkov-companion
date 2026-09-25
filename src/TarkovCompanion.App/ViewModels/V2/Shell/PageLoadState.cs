namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// Where a page's own read stands. A page is in exactly one of these, and its view shows the
/// message for that one only.
/// </summary>
/// <remarks>
/// #871/#872: Debrief and Stash used to say "has not been loaded" beside "No raids recorded yet"
/// while the read was still running, and "unavailable: &lt;exception text&gt;" above the same
/// empty message when it failed. A player reads both as "my history is gone". Each message now
/// hangs off one state, so failed can never also say empty and loading can never say either.
/// A reload of a page that has already read keeps its state until the read ends (no flash back
/// to "Loading"), and a failed page keeps its notice while Retry runs.
/// </remarks>
public enum PageLoadState
{
    /// <summary>The first read has not finished.</summary>
    Loading,

    /// <summary>The read finished and there is nothing to show.</summary>
    Empty,

    /// <summary>The read finished and there is something to show.</summary>
    Loaded,

    /// <summary>The read failed; the page shows the load-fault notice and Retry.</summary>
    Failed,
}

public static class PageLoadStates
{
    /// <summary>The state a finished read leaves the page in.</summary>
    public static PageLoadState Read(bool hasAny) => hasAny ? PageLoadState.Loaded : PageLoadState.Empty;

    /// <summary>True once a read has succeeded, so an empty-state message may be shown.</summary>
    public static bool HasRead(this PageLoadState state) => state is PageLoadState.Empty or PageLoadState.Loaded;
}
