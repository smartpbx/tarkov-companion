namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Makes a named load fail on purpose, so the failed state can be looked at.
/// </summary>
/// <remarks>
/// #453 was closed once without anyone seeing what a pane that failed to load looks like, because
/// nothing could make one fail on demand. The render tool's <c>--inject-load-fault plan,hideout</c>
/// names surfaces here; each of those loads then throws at its first line, inside its own try, and
/// everything after that is the real path: the real catch, the real notice, the real Retry.
/// Nothing in the application sets this, and with nothing set it is one array read per load.
/// </remarks>
internal static class LoadFaultInjection
{
    private static volatile string[] _surfaces = [];

    public static void Inject(IEnumerable<string> surfaces) =>
        _surfaces = [.. surfaces.Select(surface => surface.Trim()).Where(surface => surface.Length > 0)];

    public static void Clear() => _surfaces = [];

    public static void ThrowIfInjected(string surface)
    {
        if (Array.IndexOf(_surfaces, surface) >= 0)
        {
            throw new InvalidOperationException($"The '{surface}' load was made to fail on purpose (--inject-load-fault).");
        }
    }
}
