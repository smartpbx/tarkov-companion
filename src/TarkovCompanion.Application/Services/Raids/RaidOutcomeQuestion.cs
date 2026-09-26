using System.Text.Json;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// What the player did with the one question asked after a raid (#712 0-8): the answer they
/// tapped, or that they dismissed it.
/// </summary>
/// <remarks>
/// The game's logs never say whether a raid was survived (docs/research/EFT_LOG_FACTS.md), so the
/// outcome is asked, once, and the answer is the player's word: <see cref="Source"/> is always
/// <see cref="PlayerSource"/>. The answer itself is written to the raid's outcome through
/// <c>CorrectAsync</c>, whose correction event is what labels it Manual everywhere the outcome is
/// read; this event is what keeps the question from being asked a second time, including when
/// the player dismissed it without answering. Append-only like tags; no migration.
/// </remarks>
public sealed record RaidOutcomeAnswer(string? Outcome, bool Dismissed, string Source, DateTimeOffset RecordedUtc)
{
    public const string EventType = "outcome-question";

    public const string PlayerSource = "player";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RaidOutcomeAnswer Answered(RaidOutcomeBucket bucket, DateTimeOffset recordedUtc) =>
        new(RaidOutcomeQuestion.StoredText(bucket), false, PlayerSource, recordedUtc);

    public static RaidOutcomeAnswer Dismissal(DateTimeOffset recordedUtc) =>
        new(null, true, PlayerSource, recordedUtc);

    public string ToPayload() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Stored answers, oldest first; a row that will not parse is skipped, not fatal.</summary>
    public static IReadOnlyList<RaidOutcomeAnswer> ParseAll(IEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var answers = new List<RaidOutcomeAnswer>();
        foreach (var payload in payloads)
        {
            try
            {
                if (JsonSerializer.Deserialize<RaidOutcomeAnswer>(payload, JsonOptions) is { RecordedUtc: var at } answer
                    && at != default)
                {
                    answers.Add(answer);
                }
            }
            catch (JsonException)
            {
            }
        }

        return [.. answers.OrderBy(answer => answer.RecordedUtc)];
    }
}

/// <summary>When the after-raid question is asked, what an answer is stored as, and where a raid ended.</summary>
public static class RaidOutcomeQuestion
{
    /// <summary>The buttons, in the order the card shows them.</summary>
    public static IReadOnlyList<RaidOutcomeBucket> Choices { get; } =
        [RaidOutcomeBucket.Survived, RaidOutcomeBucket.Died, RaidOutcomeBucket.RunThrough, RaidOutcomeBucket.Mia];

    /// <summary>
    /// The outcome text a tap stores. Fixed English, not the translated label: the outcome column
    /// is matched by keyword (<see cref="RaidCoverage.Classify"/>, Debrief's filter) and exported.
    /// </summary>
    public static string StoredText(RaidOutcomeBucket bucket) => bucket switch
    {
        RaidOutcomeBucket.Survived => "Survived",
        RaidOutcomeBucket.Died => "Died",
        RaidOutcomeBucket.RunThrough => "Run-through",
        RaidOutcomeBucket.Mia => "MIA",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Not an answer."),
    };

    /// <summary>
    /// Whether to ask about a raid that just ended: it has no outcome yet, and the question has
    /// not already been answered or dismissed for it.
    /// </summary>
    /// <remarks>
    /// A raid that already carries an outcome is never asked about, whatever wrote it: the ask
    /// must not invite the player to overwrite a recorded fact (a post-raid summary screenshot
    /// will write one without a tap, #712 1-5). Unknown stays unknown: nothing here guesses.
    /// </remarks>
    /// <param name="recordedOutcome">The raid's outcome as stored now; null for a raid with none, or no row yet.</param>
    public static bool ShouldAsk(string? recordedOutcome, IReadOnlyList<RaidOutcomeAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return string.IsNullOrWhiteSpace(recordedOutcome) && answers.Count == 0;
    }

    /// <summary>When the outcome now on the raid was entered by hand, or null when it was not.</summary>
    public static DateTimeOffset? RecordedUtc(RaidHistoryEntry raid, IReadOnlyList<RaidCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(corrections);
        if (string.IsNullOrWhiteSpace(raid.Outcome))
        {
            return null;
        }

        return corrections
            .Where(correction => correction.Outcome is not null)
            .Select(correction => (DateTimeOffset?)correction.AtUtc)
            .Max();
    }

    /// <summary>
    /// The extract nearest the raid's last screenshot, when it is close enough to say the player
    /// ended there; an inference from position, never an observed extraction.
    /// </summary>
    /// <param name="offered">What the raid's extract-list screenshots offered; when known, only those count.</param>
    public static (string Name, double Metres)? NearestExtract(
        ScreenshotPosition? last,
        IReadOnlyList<MapExtract> extracts,
        IReadOnlyList<string>? offered,
        double withinMetres = 60)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        if (last is null)
        {
            return null;
        }

        (string Name, double Metres)? nearest = null;
        foreach (var extract in extracts)
        {
            // The catalog projects world X and world Z; its Y is the world Z (SqliteMapDefinitionCache).
            if (extract.Position is not { } point
                || (offered is { Count: > 0 } && !offered.Contains(extract.Name, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var metres = Math.Sqrt(Math.Pow(point.X - last.Position.X, 2) + Math.Pow(point.Y - last.Position.Z, 2));
            if (metres <= withinMetres && (nearest is null || metres < nearest.Value.Metres))
            {
                nearest = (extract.Name, metres);
            }
        }

        return nearest;
    }
}
