using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Core.Domain.Profiles;

/// <summary>A wipe label and when a profile started using it; null means "from the start".</summary>
public sealed record WipeMark(string Wipe, DateTimeOffset? SinceUtc);

/// <summary>
/// [#269] When a profile's wipe label changed, kept in the profile's extension JSON under
/// <c>wipes</c>, because a raid row records its mode but not its wipe.
/// </summary>
/// <remarks>
/// The rule, in one line: a raid's wipe is the label its profile had when the raid started.
/// A profile whose label never changed has no marks, and every raid is in its one wipe. The first
/// change writes the old label "from the start" and the new one from now, so a raid from before
/// the change reads as the old wipe. No migration: the extension column already exists.
/// </remarks>
public static class ProfileWipeHistory
{
    public const string Key = "wipes";

    // A label changes a few times a year; the bound only keeps a looping writer from growing the row.
    private const int MaximumMarks = 32;

    /// <summary>The marks, oldest first. Anything unreadable is skipped: this is an extension, not a contract.</summary>
    public static IReadOnlyList<WipeMark> Read(string? extensionJson)
    {
        if (string.IsNullOrWhiteSpace(extensionJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(extensionJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(Key, out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var marks = new List<WipeMark>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("wipe", out var wipe)
                    || wipe.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(wipe.GetString()))
                {
                    continue;
                }

                DateTimeOffset? since = null;
                if (entry.TryGetProperty("sinceUtc", out var sinceElement) && sinceElement.ValueKind == JsonValueKind.String)
                {
                    if (!sinceElement.TryGetDateTimeOffset(out var parsed))
                    {
                        continue;
                    }

                    since = parsed.ToUniversalTime();
                }

                marks.Add(new(wipe.GetString()!, since));
            }

            return [.. marks.OrderBy(mark => mark.SinceUtc ?? DateTimeOffset.MinValue)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The extension JSON with a change from <paramref name="previous"/> to <paramref name="next"/> at <paramref name="atUtc"/>.</summary>
    public static string Record(string extensionJson, string previous, string next, DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previous);
        ArgumentException.ThrowIfNullOrWhiteSpace(next);
        if (string.Equals(previous, next, StringComparison.Ordinal))
        {
            return extensionJson;
        }

        var marks = Read(extensionJson).ToList();
        if (marks.Count == 0 || !string.Equals(marks[^1].Wipe, previous, StringComparison.Ordinal))
        {
            // No history, or a history that no longer ends in the label being replaced: start over
            // from what is known now rather than chain onto a label this profile no longer had.
            marks = [new(previous, null)];
        }

        marks.Add(new(next, atUtc.ToUniversalTime()));
        if (marks.Count > MaximumMarks)
        {
            marks = [.. marks.Skip(marks.Count - MaximumMarks)];
            marks[0] = marks[0] with { SinceUtc = null };
        }

        var root = (JsonNode.Parse(string.IsNullOrWhiteSpace(extensionJson) ? "{}" : extensionJson) as JsonObject) ?? [];
        root[Key] = new JsonArray([
            .. marks.Select(mark => (JsonNode)new JsonObject
            {
                ["wipe"] = mark.Wipe,
                ["sinceUtc"] = mark.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }),
        ]);
        return root.ToJsonString();
    }

    /// <summary>
    /// The profile's wipe label when a raid started at <paramref name="startedUtc"/>, or null when
    /// that cannot be said (no start time on a profile whose label has changed).
    /// </summary>
    public static string? WipeAt(ProfileRecord profile, DateTimeOffset? startedUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var current = profile.Context.WipeSeason.Value;
        var marks = Read(profile.ExtensionJson);
        // A history whose last label is not the profile's label was overtaken by some other writer;
        // it no longer describes this profile, so it is not used to push raids out of the wipe.
        if (marks.Count == 0 || !string.Equals(marks[^1].Wipe, current, StringComparison.Ordinal))
        {
            return current;
        }

        if (startedUtc is not { } started)
        {
            return null;
        }

        string? found = null;
        foreach (var mark in marks)
        {
            if (mark.SinceUtc is null || mark.SinceUtc <= started.ToUniversalTime())
            {
                found = mark.Wipe;
            }
        }

        return found;
    }
}

/// <summary>How a recorded raid stands against the profile in use.</summary>
public enum RaidContextMatch
{
    Same,
    OtherProfile,
    OtherMode,
    OtherWipe,
}

/// <summary>
/// [#269] Whether a raid belongs to the active profile's mode and wipe, so history, stats and
/// exports never mix a PvE raid, or last wipe's, into this profile's numbers without saying so.
/// </summary>
/// <remarks>
/// Unknown is not different: a raid whose mode cannot be read, or whose wipe cannot be placed, is
/// kept, because an unreadable field is not evidence of another context.
/// </remarks>
public static class RaidContextRules
{
    /// <summary>A raid's stored mode (the legacy enum name: "Regular", "Pve", "PvpSeason") as a profile mode, or null.</summary>
    public static ProfileGameMode? ModeOf(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        "regular" or "pvp" => ProfileGameMode.Pvp,
        "pve" => ProfileGameMode.Pve,
        "pvpseason" or "seasonal" => ProfileGameMode.Seasonal,
        _ => null,
    };

    /// <summary>The raid's wipe label from the profile that recorded it, or null when unknown.</summary>
    public static string? WipeOf(RaidHistoryEntry raid, IEnumerable<ProfileRecord> profiles)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(profiles);
        var owner = profiles.FirstOrDefault(profile => profile.Context.Identity.ProfileId == raid.ProfileId);
        return owner is null ? null : ProfileWipeHistory.WipeAt(owner, raid.StartedUtc);
    }

    public static RaidContextMatch Match(RaidHistoryEntry raid, ProfileRecord active)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(active);
        if (raid.ProfileId != active.Context.Identity.ProfileId)
        {
            return RaidContextMatch.OtherProfile;
        }

        if (ModeOf(raid.Mode) is { } mode && active.Context.Mode != ProfileGameMode.Unknown && mode != active.Context.Mode)
        {
            return RaidContextMatch.OtherMode;
        }

        return ProfileWipeHistory.WipeAt(active, raid.StartedUtc) is { } wipe
            && !string.Equals(wipe, active.Context.WipeSeason.Value, StringComparison.Ordinal)
                ? RaidContextMatch.OtherWipe
                : RaidContextMatch.Same;
    }

    public static bool InContext(RaidHistoryEntry raid, ProfileRecord active) => Match(raid, active) == RaidContextMatch.Same;
}

/// <summary>The profiles as they are now, for reading raids against. Null <see cref="Active"/> means no filtering is possible.</summary>
public sealed record RaidContextView(ProfileRecord? Active, IReadOnlyList<ProfileRecord> Profiles)
{
    public static readonly RaidContextView None = new(null, []);

    public string? WipeOf(RaidHistoryEntry raid) => RaidContextRules.WipeOf(raid, Profiles);

    /// <summary>True when there is no active profile to compare with.</summary>
    public bool InActive(RaidHistoryEntry raid) => Active is null || RaidContextRules.InContext(raid, Active);
}

/// <summary>Where history, stats and exports read the current <see cref="RaidContextView"/> from.</summary>
public interface IRaidContextSource
{
    RaidContextView Current();
}
