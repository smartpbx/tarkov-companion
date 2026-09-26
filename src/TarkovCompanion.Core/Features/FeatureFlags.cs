namespace TarkovCompanion.Core.Features;

/// <summary>Which builds a player is on, as far as switching a feature on by default goes (#314).</summary>
/// <remarks>
/// Rough is the relay's test feed everybody installs today. Stable is the signed ring #280 will
/// add; nothing reports it yet, so its defaults are what a feature ships with once it is there.
/// Dev is a build run from a folder rather than installed, or pointed at a custom feed.
/// </remarks>
public enum ReleaseRing
{
    Dev,
    Rough,
    Stable,
}

/// <summary>Reads a ring's name, tolerating case and an unknown value.</summary>
public static class ReleaseRings
{
    /// <summary>False for anything that is not one of the three names; the caller keeps its fallback.</summary>
    public static bool TryParse(string? value, out ReleaseRing ring)
    {
        ring = ReleaseRing.Rough;
        return !string.IsNullOrWhiteSpace(value)
            && !int.TryParse(value, out _)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out ring)
            && Enum.IsDefined(ring);
    }
}

/// <summary>One reversible switch: its key in the override file, what it turns on, and where it defaults on.</summary>
/// <param name="Key">The name in Config/feature-flags.json. Never renamed once shipped, or an override is lost.</param>
/// <param name="Title">What Setup calls it.</param>
/// <param name="Description">One line on what turning it off hides.</param>
/// <param name="OwnerIssue">The issue that owns the feature, so a stale flag has somebody to ask.</param>
/// <param name="OnInDev">Default on a build run from a folder.</param>
/// <param name="OnInRough">Default on the rough test feed.</param>
/// <param name="OnInStable">Default on the signed ring (#280).</param>
/// <param name="NeedsRestart">True when the feature reads the flag once, as it starts.</param>
public sealed record FeatureFlagDefinition(
    string Key,
    string Title,
    string Description,
    int OwnerIssue,
    bool OnInDev,
    bool OnInRough,
    bool OnInStable,
    bool NeedsRestart)
{
    public bool DefaultFor(ReleaseRing ring) => ring switch
    {
        ReleaseRing.Dev => OnInDev,
        ReleaseRing.Stable => OnInStable,
        _ => OnInRough,
    };
}

/// <summary>The flags that exist. A feature checks one with <c>IFeatureFlags.IsOn(Flag.DrawMode)</c>.</summary>
/// <remarks>
/// A flag is for a feature new enough that turning it off without a new build is worth having.
/// When the feature has settled, delete the flag and the check together.
/// </remarks>
public static class Flag
{
    public static readonly FeatureFlagDefinition DrawMode = new(
        "draw-mode",
        "Draw on the Raid map",
        "The pencil on the Raid map. Off hides it; lines already drawn stay.",
        OwnerIssue: 286,
        OnInDev: true,
        OnInRough: true,
        OnInStable: false,
        // [#902 P3] Switched from Raid › View › Drawing tools, and applied at once: the pencil
        // is only ever read when the mode strip is drawn and when Draw mode is asked for.
        NeedsRestart: false);

    public static readonly FeatureFlagDefinition TabletReviewCards = new(
        "tablet-review-cards",
        "Stash and Flea cards on the tablet",
        "Sends each Stash scan and flea screen to a paired tablet.",
        OwnerIssue: 290,
        OnInDev: true,
        OnInRough: true,
        OnInStable: false,
        NeedsRestart: true);

    /// <summary>[#712 0-9] The brief the Raid panel shows while the game log says matching or loading.</summary>
    public static readonly FeatureFlagDefinition PreRaidBrief = new(
        "preraid-brief",
        "Pre-raid brief",
        "The Raid panel's brief while you match. Off keeps the usual cards.",
        OwnerIssue: 712,
        OnInDev: true,
        OnInRough: true,
        OnInStable: false,
        NeedsRestart: false);

    public static IReadOnlyList<FeatureFlagDefinition> All { get; } = [DrawMode, TabletReviewCards, PreRaidBrief];
}

/// <summary>Whether a feature is on for this run.</summary>
public interface IFeatureFlags
{
    ReleaseRing Ring { get; }

    bool IsOn(FeatureFlagDefinition flag);
}
