namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed record TarkovDevJsonClientOptions
{
    public Uri BaseAddress { get; init; } = new("https://json.tarkov.dev/");

    /// <summary>
    /// A group server holding the same catalog, tried before upstream.
    /// </summary>
    /// <remarks>
    /// Every client otherwise syncs several megabytes of identical answers on its own
    /// connection; a group of five does that five times. Where a group already runs a server,
    /// it can hold one copy and hand out a content-addressed snapshot, and a client that
    /// already has that snapshot gets a 304 and no body at all.
    ///
    /// Null by default, and a failure against it is never fatal: the upstream address below is
    /// always tried afterwards. The moment this becomes required, the server stops being an
    /// optimisation and becomes something the application cannot run without, which is not a
    /// trade this application makes.
    /// </remarks>
    public Uri? MirrorAddress { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);

    // The live items catalog alone is ~16.7 MB, and the translated merge (the raw document
    // re-parsed with per-language text substituted in) runs another 3-5% over that — 16 MiB
    // left the merge with no headroom at all and refused every "items" refresh on 2026-09-17.
    // 32 MiB keeps the same protective ceiling against a runaway payload while giving the
    // catalog room to grow before this has to move again.
    public long MaximumResponseBytes { get; init; } = 32L * 1024 * 1024;

    public int MaximumJsonDepth { get; init; } = 32;

    public TimeSpan StaticFreshFor { get; init; } = TimeSpan.FromHours(9);

    public TimeSpan PriceFreshFor { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxAttempts { get; init; } = 3;

    /// <summary>Re-evaluated for every request so a long-running process can observe reconnect.</summary>
    public Func<bool> OfflineProbe { get; init; } = static () =>
        string.Equals(Environment.GetEnvironmentVariable("TARKOV_COMPANION_OFFLINE"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("TARKOV_COMPANION_OFFLINE"), "true", StringComparison.OrdinalIgnoreCase);

    public TimeSpan OfflineReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public int MaximumOfflineReconnectAttempts { get; init; } = 6;

    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The json.tarkov.dev base address must be absolute.", nameof(BaseAddress));
        }

        if (MirrorAddress is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The catalog mirror address must be absolute.", nameof(MirrorAddress));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaximumResponseBytes is < 1024 or > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }

        if (MaximumJsonDepth is < 4 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumJsonDepth));
        }

        if (StaticFreshFor <= TimeSpan.Zero || PriceFreshFor <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StaticFreshFor), "Cache freshness windows must be positive.");
        }

        if (MaxAttempts is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "At most three attempts are allowed.");
        }

        if (InitialRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));
        }


        ArgumentNullException.ThrowIfNull(OfflineProbe);
        if (OfflineReconnectDelay < TimeSpan.Zero || OfflineReconnectDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(OfflineReconnectDelay));
        }

        if (MaximumOfflineReconnectAttempts is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOfflineReconnectAttempts));
        }
    }
}
