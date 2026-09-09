namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed record TarkovDevJsonClientOptions
{
    public Uri BaseAddress { get; init; } = new("https://json.tarkov.dev/");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan StaticFreshFor { get; init; } = TimeSpan.FromHours(9);

    public TimeSpan PriceFreshFor { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxAttempts { get; init; } = 3;

    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The json.tarkov.dev base address must be absolute.", nameof(BaseAddress));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
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
    }
}
