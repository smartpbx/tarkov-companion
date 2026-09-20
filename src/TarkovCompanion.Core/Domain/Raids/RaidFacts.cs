using System.Text.Json;

namespace TarkovCompanion.Core.Domain.Raids;

/// <summary>Where a fact about a raid came from, in the four terms Debrief and its export use.</summary>
/// <remarks>
/// Coarser than <c>EvidenceSourceClass</c>, on purpose: this is what a player reading a debrief
/// needs to tell apart, not the full lineage. <b>Observed</b> is something the game wrote (a log
/// line, a screenshot's own name). <b>Inferred</b> is the companion working something out from
/// observations or settings: it did not see it. <b>Estimated</b> is a figure that is a bound or a
/// price rather than a reading. <b>Manual</b> is something the player typed.
/// </remarks>
public enum RaidFactKind
{
    /// <summary>No value, so no source: an empty field is not "observed nothing".</summary>
    Unknown = 0,
    Observed,
    Inferred,
    Estimated,
    Manual,
}

public static class RaidFactKinds
{
    /// <summary>The short word shown beside a fact, or empty where there is nothing to label.</summary>
    public static string Label(this RaidFactKind kind) => kind switch
    {
        RaidFactKind.Observed => "Observed",
        RaidFactKind.Inferred => "Inferred",
        RaidFactKind.Estimated => "Estimate",
        RaidFactKind.Manual => "Manual",
        _ => string.Empty,
    };

    /// <summary>The value written to an export: lower case and stable, or null where there is no source to name.</summary>
    public static string? Slug(this RaidFactKind kind) => kind switch
    {
        RaidFactKind.Observed => "observed",
        RaidFactKind.Inferred => "inferred",
        RaidFactKind.Estimated => "estimated",
        RaidFactKind.Manual => "manual",
        _ => null,
    };
}

/// <summary>
/// The outcome and notes the companion itself writes when it finds a raid it never saw end.
/// </summary>
/// <remarks>
/// One home for the text, because Debrief tells "the companion wrote this" from "the player wrote
/// this" by recognising it. Two copies of a sentence that has to match are how that stops working.
/// </remarks>
public static class RaidClosure
{
    public const string ClosedOnRestartOutcome = "Closed on restart";

    public const string ClosedOnRestartNotes = "The companion was not running when this raid ended.";
}

/// <summary>One field's value before and after a correction.</summary>
public sealed record RaidFieldChange(string? From, string? To);

/// <summary>
/// A player's correction to a raid's outcome or notes, kept as an event so the field can say it was
/// typed by hand and what it said before.
/// </summary>
/// <remarks>
/// A field is present only if it changed: saving the form with nothing edited records nothing.
/// Append-only. The row's current value is still what the raid says; this is the reason it says it.
/// </remarks>
public sealed record RaidCorrection(DateTimeOffset AtUtc, RaidFieldChange? Outcome, RaidFieldChange? Notes)
{
    /// <summary>The event type this is stored under in a raid's history.</summary>
    public const string EventType = "correction";

    /// <summary>What saving <paramref name="outcome"/> and <paramref name="notes"/> over <paramref name="before"/> changes, or null if nothing.</summary>
    public static RaidCorrection? Between(RaidHistoryEntry before, string? outcome, string? notes, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(before);
        var outcomeChange = Change(before.Outcome, outcome);
        var notesChange = Change(before.Notes, notes);
        return outcomeChange is null && notesChange is null ? null : new RaidCorrection(atUtc, outcomeChange, notesChange);
    }

    public string ToPayload() => JsonSerializer.Serialize(this);

    /// <summary>Reads stored payloads, skipping any that will not parse: one unreadable correction must not hide the raid.</summary>
    public static IReadOnlyList<RaidCorrection> ParseAll(IEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var corrections = new List<RaidCorrection>();
        foreach (var payload in payloads)
        {
            try
            {
                if (JsonSerializer.Deserialize<RaidCorrection>(payload) is { } correction)
                {
                    corrections.Add(correction);
                }
            }
            catch (JsonException)
            {
            }
        }

        return corrections;
    }

    private static RaidFieldChange? Change(string? from, string? to) =>
        string.Equals(from, to, StringComparison.Ordinal) ? null : new RaidFieldChange(from, to);
}

/// <summary>What kind of evidence stands behind each field of one raid.</summary>
public sealed record RaidFactSources(
    RaidFactKind Map,
    RaidFactKind Mode,
    RaidFactKind Started,
    RaidFactKind Ended,
    RaidFactKind Outcome,
    RaidFactKind Notes)
{
    /// <summary>How long it lasted: a reading only if both ends were read. Estimated where the end was worked out.</summary>
    public RaidFactKind Duration => Started == RaidFactKind.Unknown || Ended == RaidFactKind.Unknown
        ? RaidFactKind.Unknown
        : Ended == RaidFactKind.Observed && Started == RaidFactKind.Observed
            ? RaidFactKind.Observed
            : RaidFactKind.Estimated;
}

/// <summary>
/// Says where each field of a raid came from, from the ways the record can be written.
/// </summary>
/// <remarks>
/// The map and the start and end times come from the game's log. The mode is the active profile's
/// setting when the raid was first seen, not something the raid announced, so it is inferred. The
/// game records no outcome or notes, so a normal raid end writes neither: the only writers of
/// either are the companion closing a raid it never saw end (fixed text, and an end time that is
/// when it noticed, so that end is inferred too) and a player's correction. That is what lets an
/// older row, written before corrections were kept as events, be classified from its text.
/// A raid whose fixed text was later overwritten is remembered by the correction that overwrote it.
/// </remarks>
public static class RaidFactRules
{
    public static RaidFactSources Classify(RaidHistoryEntry raid, IReadOnlyList<RaidCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(corrections);

        var closedOnRestart = raid.Outcome == RaidClosure.ClosedOnRestartOutcome
            || raid.Notes == RaidClosure.ClosedOnRestartNotes
            || corrections.Any(correction =>
                correction.Outcome?.From == RaidClosure.ClosedOnRestartOutcome
                || correction.Notes?.From == RaidClosure.ClosedOnRestartNotes);
        return new(
            Present(raid.MapId, RaidFactKind.Observed),
            Present(raid.Mode, RaidFactKind.Inferred),
            raid.StartedUtc is null ? RaidFactKind.Unknown : RaidFactKind.Observed,
            raid.EndedUtc is null ? RaidFactKind.Unknown : closedOnRestart ? RaidFactKind.Inferred : RaidFactKind.Observed,
            Written(raid.Outcome, RaidClosure.ClosedOnRestartOutcome, corrections.Any(correction => correction.Outcome is not null)),
            Written(raid.Notes, RaidClosure.ClosedOnRestartNotes, corrections.Any(correction => correction.Notes is not null)));
    }

    private static RaidFactKind Present(string? value, RaidFactKind kind) =>
        string.IsNullOrWhiteSpace(value) ? RaidFactKind.Unknown : kind;

    private static RaidFactKind Written(string? value, string companionText, bool corrected) =>
        string.IsNullOrWhiteSpace(value)
            ? RaidFactKind.Unknown
            : corrected ? RaidFactKind.Manual : value == companionText ? RaidFactKind.Inferred : RaidFactKind.Manual;
}
