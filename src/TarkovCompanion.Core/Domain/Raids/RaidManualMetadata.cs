namespace TarkovCompanion.Core.Domain.Raids;

/// <summary>
/// What the player typed by hand about a raid: kills by who they were fighting, and what they
/// carried out. The game announces neither (see <see cref="RaidFactRules"/>'s remark on outcome),
/// so every field here is <see cref="RaidFactKind.Manual"/> wherever it is shown.
/// </summary>
/// <remarks>
/// Stored in <c>raids.manual_metadata_json</c>, a column every database has carried since
/// migration 0001 and nothing had written to until this. A field is null until the player enters
/// it; zero is a value ("no PMC kills"), not the same as never having been asked.
/// </remarks>
public sealed record RaidManualMetadata(int? PmcKills, int? ScavKills, int? BossKills, long? ValueRoubles)
{
    public static readonly RaidManualMetadata Empty = new(null, null, null, null);

    public bool IsEmpty => PmcKills is null && ScavKills is null && BossKills is null && ValueRoubles is null;
}
