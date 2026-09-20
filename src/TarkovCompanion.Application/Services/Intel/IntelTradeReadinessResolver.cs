namespace TarkovCompanion.Application.Services.Intel;

/// <summary>
/// Whether the active profile's own station or trader level is enough to take a craft or barter
/// right now, plus the level actually on record when that answer is "no".
/// </summary>
/// <param name="Readiness">Ready, Locked, or Unknown — never Locked for a gap in the profile's own data.</param>
/// <param name="RecordedLevel">
/// The profile's own level for that station/trader, where one is on record. Null whenever
/// <see cref="Readiness"/> is <see cref="IntelTradeReadiness.Unknown"/> — there is nothing to
/// report — and also null for <see cref="IntelTradeReadiness.Ready"/> reached because the trade
/// states no real requirement, which never looked anything up to report.
/// </param>
public readonly record struct IntelTradeReadinessResult(IntelTradeReadiness Readiness, int? RecordedLevel);

/// <summary>
/// Resolves a craft or barter's readiness against the active profile, pure and DB-free so the
/// three states — and the one rule that keeps a data gap from reading as "locked" — are a unit
/// test's business, not a fixture's.
/// </summary>
/// <remarks>
/// <para>
/// A player reported every row reading "Locked", including ones that ask for nothing more than
/// loyalty level 1 or a station level the profile had simply never recorded. Both were the exact
/// fault the brief ruled out: an unknown level is not evidence of a level 0 the player has not
/// reached, and <c>IReadOnlyDictionary.GetValueOrDefault</c> — which reads a missing key as 0 —
/// cannot tell "never recorded" from "recorded as 0" apart. Every lookup here uses
/// <c>TryGetValue</c> instead, so a station or trader the profile has never heard of about a
/// craft/barter resolves to <see cref="IntelTradeReadiness.Unknown"/>, never
/// <see cref="IntelTradeReadiness.Locked"/>.
/// </para>
/// <para>
/// Loyalty level 1 is where every trader starts (it is standing, not something built), so a
/// barter that asks for it — or states no requirement the feed bothered to name — is open to
/// anybody regardless of what the profile does or does not know. A hideout station gets no such
/// shortcut: every station starts at level 0 and has to be built, so "level 1" is a real,
/// unearned requirement there.
/// </para>
/// </remarks>
public static class IntelTradeReadinessResolver
{
    /// <summary>The loyalty level every trader relationship starts at, requiring nothing to reach.</summary>
    public const int DefaultTraderLoyaltyLevel = 1;

    public static IntelTradeReadinessResult ForBarter(
        int? minimumTraderLevel,
        string? traderId,
        IReadOnlyDictionary<string, int> traderLevels)
    {
        ArgumentNullException.ThrowIfNull(traderLevels);

        if (minimumTraderLevel is not { } required || required <= DefaultTraderLoyaltyLevel)
        {
            return new(IntelTradeReadiness.Ready, null);
        }

        if (string.IsNullOrEmpty(traderId))
        {
            return new(IntelTradeReadiness.Unknown, null);
        }

        return traderLevels.TryGetValue(traderId, out var level)
            ? new(level >= required ? IntelTradeReadiness.Ready : IntelTradeReadiness.Locked, level)
            : new(IntelTradeReadiness.Unknown, null);
    }

    public static IntelTradeReadinessResult ForCraft(
        string? stationId,
        int? requiredLevel,
        IReadOnlyDictionary<string, int> stationLevels)
    {
        ArgumentNullException.ThrowIfNull(stationLevels);

        if (string.IsNullOrEmpty(stationId) || requiredLevel is not { } required)
        {
            return new(IntelTradeReadiness.Unknown, null);
        }

        return stationLevels.TryGetValue(stationId, out var level)
            ? new(level >= required ? IntelTradeReadiness.Ready : IntelTradeReadiness.Locked, level)
            : new(IntelTradeReadiness.Unknown, null);
    }
}
