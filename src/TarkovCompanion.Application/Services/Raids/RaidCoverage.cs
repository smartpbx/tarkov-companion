namespace TarkovCompanion.Application.Services.Raids;

/// <summary>What a raid's free-text outcome says happened, by keyword.</summary>
/// <remarks>
/// The game never writes an outcome (RaidFactRules), so the text is whatever the player typed.
/// Debrief's outcome filter matches the same keywords; this decides one bucket for counting.
/// </remarks>
public enum RaidOutcomeBucket { Unknown, Survived, RunThrough, Mia, Died }

/// <summary>One raid as the per-map coverage and the charts need it.</summary>
/// <param name="OfferedExtracts">The extracts the raid's photographed lists named; null when none was photographed.</param>
/// <param name="UsedExtract">The extract the player says they took, or null.</param>
/// <param name="ValueRoubles">The value brought out, entered by hand, or null.</param>
public sealed record RaidCoverageInput(
    Guid RaidId,
    string? MapId,
    DateTimeOffset? StartedUtc,
    string? Outcome,
    IReadOnlyList<string>? OfferedExtracts,
    string? UsedExtract,
    long? ValueRoubles);

/// <summary>How one map has gone across the raids given.</summary>
/// <param name="OutcomesRecorded">Raids with an outcome that falls in a bucket; the survival rate's denominator.</param>
/// <param name="RaidsWithOffered">Raids that recorded what extracts they offered.</param>
/// <param name="UsedOfOffered">Distinct extracts taken, in a raid that offered them, on this map.</param>
/// <param name="Offered">Distinct extracts offered across the raids that recorded a list.</param>
/// <param name="UsedUnchecked">Raids with an extract taken but no offered list to check it against.</param>
public sealed record RaidMapCoverage(
    string MapId,
    int Raids,
    int Extracted,
    int Died,
    int OutcomesRecorded,
    int RaidsWithOffered,
    IReadOnlyList<string> UsedOfOffered,
    IReadOnlyList<string> Offered,
    int UsedUnchecked)
{
    /// <summary>Extracted over raids with a recorded outcome, or null when none has one.</summary>
    public double? SurvivalRate => OutcomesRecorded == 0 ? null : (double)Extracted / OutcomesRecorded;
}

/// <summary>One point of the value-per-raid series.</summary>
public sealed record RaidValuePoint(Guid RaidId, string? MapId, DateTimeOffset StartedUtc, long ValueRoubles);

public static class RaidCoverage
{
    public static RaidOutcomeBucket Classify(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return RaidOutcomeBucket.Unknown;
        }

        bool Has(string word) => outcome.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("surviv"))
        {
            return RaidOutcomeBucket.Survived;
        }

        if (Has("run") || Has("transit"))
        {
            return RaidOutcomeBucket.RunThrough;
        }

        if (Has("mia") || Has("missing"))
        {
            return RaidOutcomeBucket.Mia;
        }

        return Has("die") || Has("kill") ? RaidOutcomeBucket.Died : RaidOutcomeBucket.Unknown;
    }

    /// <summary>Whether the bucket means the player got out: survived or ran through.</summary>
    public static bool IsExtracted(RaidOutcomeBucket bucket) =>
        bucket is RaidOutcomeBucket.Survived or RaidOutcomeBucket.RunThrough;

    /// <summary>Per-map coverage, busiest map first; raids with no map are left out.</summary>
    public static IReadOnlyList<RaidMapCoverage> ByMap(IEnumerable<RaidCoverageInput> raids)
    {
        ArgumentNullException.ThrowIfNull(raids);
        return
        [
            .. raids
                .Where(raid => !string.IsNullOrWhiteSpace(raid.MapId))
                .GroupBy(raid => raid.MapId!, StringComparer.OrdinalIgnoreCase)
                .Select(Summarize)
                .OrderByDescending(map => map.Raids)
                .ThenBy(map => map.MapId, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Raids with a value entered, oldest first, which is the order a chart reads them in.</summary>
    public static IReadOnlyList<RaidValuePoint> ValueSeries(IEnumerable<RaidCoverageInput> raids)
    {
        ArgumentNullException.ThrowIfNull(raids);
        return
        [
            .. raids
                .Where(raid => raid.ValueRoubles is not null && raid.StartedUtc is not null)
                .Select(raid => new RaidValuePoint(raid.RaidId, raid.MapId, raid.StartedUtc!.Value, raid.ValueRoubles!.Value))
                .OrderBy(point => point.StartedUtc)
                .ThenBy(point => point.RaidId),
        ];
    }

    private static RaidMapCoverage Summarize(IGrouping<string, RaidCoverageInput> group)
    {
        var raids = group.ToArray();
        var buckets = raids.Select(raid => Classify(raid.Outcome)).ToArray();
        var offered = new List<string>();
        var offeredSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new List<string>();
        var usedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unchecked_ = 0;
        foreach (var raid in raids)
        {
            foreach (var name in raid.OfferedExtracts ?? [])
            {
                if (offeredSeen.Add(name))
                {
                    offered.Add(name);
                }
            }

            if (raid.UsedExtract is not { Length: > 0 } taken)
            {
                continue;
            }

            // The numerator counts an extract only against the list that raid itself offered:
            // one taken where no list was photographed cannot be checked, and is counted apart.
            if (raid.OfferedExtracts is not { Count: > 0 } raidOffered)
            {
                unchecked_++;
            }
            else if (raidOffered.Contains(taken, StringComparer.OrdinalIgnoreCase) && usedSeen.Add(taken))
            {
                used.Add(taken);
            }
        }

        return new RaidMapCoverage(
            group.Key,
            raids.Length,
            buckets.Count(IsExtracted),
            buckets.Count(bucket => bucket == RaidOutcomeBucket.Died),
            buckets.Count(bucket => bucket != RaidOutcomeBucket.Unknown),
            raids.Count(raid => raid.OfferedExtracts is { Count: > 0 }),
            used,
            offered,
            unchecked_);
    }
}
