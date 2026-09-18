namespace TarkovCompanion.Core.Domain.Raids;

/// <summary>
/// How long matchmaking and loading took before a raid began, as the game measured it.
/// </summary>
/// <remarks>
/// docs/research/EFT_LOG_FACTS.md records this as present and unused: the game writes
/// <c>MatchingCompleted:&lt;elapsed&gt; real:&lt;seconds&gt; diff:&lt;seconds&gt;</c> to
/// <c>application</c> shortly before a raid loads, and nothing read the <c>real</c> figure back.
/// It arrives before the raid it belongs to has an id, so it is held until that raid starts.
/// </remarks>
/// <param name="RealSeconds">The `real` figure, in seconds — wall-clock time including matchmaking.</param>
/// <param name="ObservedUtc">When the line was read.</param>
public sealed record LoadTimeObservation(double RealSeconds, DateTimeOffset ObservedUtc);
