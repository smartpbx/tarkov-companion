namespace TarkovCompanion.Core.Domain.Profiles;

/// <summary>The game mode the game itself says this session is in, from its <c>Session mode:</c> line.</summary>
/// <remarks>
/// The game writes <c>Session mode: PvpSeason</c> once per launch, before any raid (#403: 6 of 6
/// sessions on 1.1.5.1.47510). It is the only line that names the mode, so it is what lets the
/// active companion profile follow the game (#712 decision 4). Only the three spellings the
/// companion already stores are mapped (<see cref="RaidContextRules.ModeOf"/>); any other value
/// keeps <see cref="Mode"/> null, which means "do not switch", never a guess.
/// </remarks>
/// <param name="GameValue">The game's own word, verbatim ("PvpSeason").</param>
/// <param name="Mode">The profile mode it maps to, or null for a value never observed.</param>
/// <param name="ObservedUtc">When the line was read.</param>
public sealed record GameSessionMode(string GameValue, ProfileGameMode? Mode, DateTimeOffset ObservedUtc);
