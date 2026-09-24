using System.Text.Json;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>The extract a player says they left a raid by.</summary>
/// <remarks>
/// The game's logs never name the extract taken (docs/research/EFT_LOG_FACTS.md), so this is
/// typed or picked by hand in Debrief and is always a manual fact. It is an append-only raid event
/// rather than a column: the newest one wins, an empty name clears it, and no migration is needed.
/// </remarks>
public sealed record RaidExtractUsed(string? Extract, DateTimeOffset RecordedUtc)
{
    public const string EventType = "extract-used";
    public const int MaximumLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? Normalize(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaximumLength || name.Any(char.IsControl) ? null : name;
    }

    public string ToPayload() => JsonSerializer.Serialize(this with { Extract = Normalize(Extract) }, JsonOptions);

    /// <summary>The newest recorded extract, or null when none was recorded or the newest cleared it.</summary>
    public static string? Latest(IEnumerable<string> payloads)
    {
        RaidExtractUsed? latest = null;
        foreach (var payload in payloads)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RaidExtractUsed>(payload, JsonOptions);
                if (entry is not null && entry.RecordedUtc != default && (latest is null || entry.RecordedUtc >= latest.RecordedUtc))
                {
                    latest = entry;
                }
            }
            catch (JsonException)
            {
                // A hand-edited row must not hide the extracts recorded on every other raid.
            }
        }

        return Normalize(latest?.Extract);
    }
}

/// <summary>A raid set aside from Debrief's lists and totals, and restorable.</summary>
/// <remarks>
/// Distinct from delete (#549): a delete is undoable for one press and then the row is gone; an
/// archive keeps every fact and can be reversed at any time. Append-only like tags, newest wins.
/// </remarks>
public sealed record RaidArchive(bool Archived, DateTimeOffset ChangedUtc)
{
    public const string EventType = "archive";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToPayload() => JsonSerializer.Serialize(this, JsonOptions);

    public static bool IsArchived(IEnumerable<string> payloads)
    {
        RaidArchive? latest = null;
        foreach (var payload in payloads)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RaidArchive>(payload, JsonOptions);
                if (entry is not null && entry.ChangedUtc != default && (latest is null || entry.ChangedUtc >= latest.ChangedUtc))
                {
                    latest = entry;
                }
            }
            catch (JsonException)
            {
            }
        }

        return latest?.Archived ?? false;
    }
}

/// <summary>Reads the extract lists a raid recorded when the player photographed them.</summary>
/// <remarks>
/// <see cref="Runtime.RaidHistoryCommand.RecordExtracts"/> writes one "extracts" event per
/// photographed list, as a bare JSON array of <see cref="ActiveExtract"/> in default (PascalCase)
/// options. A raid with none has no record of what it offered, which is not the same as offering
/// nothing, so this returns null rather than an empty list for it.
/// </remarks>
public static class RaidOfferedExtracts
{
    public const string EventType = "extracts";

    public static IReadOnlyList<string>? Union(IEnumerable<string> payloads)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recorded = false;
        foreach (var payload in payloads)
        {
            try
            {
                var list = JsonSerializer.Deserialize<ActiveExtract[]>(payload);
                // A photographed list the scan read no line from says nothing about what was offered.
                if (list is not { Length: > 0 })
                {
                    continue;
                }

                recorded = true;
                foreach (var extract in list)
                {
                    if (RaidExtractUsed.Normalize(extract?.Name) is { } name && seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return recorded ? names : null;
    }
}
