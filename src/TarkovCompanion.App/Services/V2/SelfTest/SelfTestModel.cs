using System.Globalization;
using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>What one capability's probe concluded, having actually exercised that capability.</summary>
/// <remarks>
/// <see cref="Unknown"/> is deliberately not a pass. Setup already shows readiness; the complaint
/// this page exists to answer is that a capability nobody had tested looked exactly like one that
/// worked. An empty refresh reported "2 endpoint refresh(es) failed" with no names, a stale log
/// folder reported nothing at all, and both were invisible until something was missing. A probe
/// that could not measure its capability says so, and the run does not call itself healthy.
/// </remarks>
public enum SelfTestOutcome
{
    /// <summary>Not run yet.</summary>
    Pending = 1,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Measured, and the capability did what it claims to do.</summary>
    Pass,

    /// <summary>Measured, and it did not.</summary>
    Fail,

    /// <summary>Not measurable from here, with the reason said out loud.</summary>
    Unknown,

    /// <summary>
    /// Measurable, but only once the player does something. Nothing is wrong yet.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] The screenshot probe's live state. It used to hold the whole run open
    /// for its patience and then report an unknown, which read as "this failed and it was your
    /// fault for being slow". Waiting is its own thing: the rest of the run has already finished,
    /// this one is still open, and it will settle by itself the moment a screenshot appears.
    /// </remarks>
    Waiting,
}

/// <summary>
/// One line of the report: a thing that was measured, beside what it was measured from.
/// </summary>
/// <remarks>
/// The split is the whole point of this page. A green tick says somebody looked; it does not say
/// what they looked at, when, or whether the answer is an hour old. <paramref name="Source"/> is
/// never "checked" on its own — it names the file, folder, table, or endpoint the number came
/// out of, and the clock it was read on.
/// </remarks>
/// <param name="Text">The fact, in the words Setup would use.</param>
/// <param name="Source">What was read, and when.</param>
public sealed record SelfTestFact(string Text, string Source)
{
    public override string ToString() => $"{Text} — {Source}";
}

/// <summary>One capability's result: its verdict, a one-line headline, and the facts behind it.</summary>
public sealed record SelfTestCapability(
    string Id,
    string Title,
    SelfTestOutcome Outcome,
    string Headline,
    IReadOnlyList<SelfTestFact> Facts,
    TimeSpan Took)
{
    public static SelfTestCapability Waiting(string id, string title, string headline) =>
        new(id, title, SelfTestOutcome.Pending, headline, [], TimeSpan.Zero);

    public static SelfTestCapability Running(string id, string title, string headline) =>
        new(id, title, SelfTestOutcome.Running, headline, [], TimeSpan.Zero);

    /// <summary>Open, and waiting for the player rather than for the machine.</summary>
    public static SelfTestCapability WaitingFor(
        string id,
        string title,
        string headline,
        IReadOnlyList<SelfTestFact>? facts = null) =>
        new(id, title, SelfTestOutcome.Waiting, headline, facts ?? [], TimeSpan.Zero);
}

/// <summary>Everything one press of the self-test found.</summary>
public sealed record SelfTestSummary(
    DateTimeOffset StartedUtc,
    TimeSpan Took,
    IReadOnlyList<SelfTestCapability> Capabilities)
{
    public static SelfTestSummary Empty { get; } = new(DateTimeOffset.UnixEpoch, TimeSpan.Zero, []);

    public int PassCount => Capabilities.Count(capability => capability.Outcome == SelfTestOutcome.Pass);

    public int FailCount => Capabilities.Count(capability => capability.Outcome == SelfTestOutcome.Fail);

    public int UnknownCount => Capabilities.Count(capability => capability.Outcome == SelfTestOutcome.Unknown);

    /// <summary>The run's verdict. One failure fails the run; an untested capability is not a pass.</summary>
    public int WaitingCount => Capabilities.Count(capability => capability.Outcome == SelfTestOutcome.Waiting);

    public SelfTestOutcome Outcome =>
        Capabilities.Count == 0 ? SelfTestOutcome.Pending
        : Capabilities.Any(capability => capability.Outcome is SelfTestOutcome.Pending or SelfTestOutcome.Running) ? SelfTestOutcome.Running
        : FailCount > 0 ? SelfTestOutcome.Fail
        : UnknownCount > 0 ? SelfTestOutcome.Unknown
        // Below the two verdicts that need somebody, above a clean pass: a run still waiting on a
        // screenshot has found nothing wrong, and has not finished either.
        : WaitingCount > 0 ? SelfTestOutcome.Waiting
        : SelfTestOutcome.Pass;

    public string Headline(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return Capabilities.Count == 0
            ? SetupText.ProbeNothingTested
            : WaitingCount > 0
                ? SetupText.ProbeHeadlineWaiting(culture, PassCount, FailCount, UnknownCount, WaitingCount, Duration(Took, culture))
                : SetupText.ProbeHeadline(culture, PassCount, FailCount, UnknownCount, Duration(Took, culture));
    }

    /// <summary>
    /// The whole run as text, ready to paste into a conversation about a problem.
    /// </summary>
    /// <remarks>
    /// Local diagnostic output, which docs/SAFETY.md allows to name local paths — the paths are
    /// most of the answer when the question is "why did it read the wrong folder". This is not
    /// the shareable bundle: what leaves the machine over the relay is
    /// <see cref="ToSupportFacts"/>, which carries no path, name, or coordinate.
    /// </remarks>
    public string ToText(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var text = new StringBuilder(capacity: 2_000);
        text.Append(SetupText.ProbeTextHeading).AppendLine();
        text.AppendLine();
        // Every time in this text is the player's own clock. The offset is named once, here, because
        // the text is pasted to someone in another zone who has to know which clock it means.
        text.Append(SetupText.ProbeTextStarted(culture, LocalTime.SortableSeconds(StartedUtc), LocalTime.Offset(StartedUtc))).Append(Headline(culture)).AppendLine();
        foreach (var capability in Capabilities)
        {
            text.AppendLine();
            text.Append("### ").Append(capability.Title).Append(" — ").Append(Word(capability.Outcome)).AppendLine();
            text.Append(capability.Headline).AppendLine();
            foreach (var fact in capability.Facts)
            {
                text.Append("- ").Append(fact.Text).AppendLine();
                text.Append("  (").Append(fact.Source).Append(')').AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The same run projected into the closed vocabulary an outbound report may carry.
    /// </summary>
    /// <remarks>
    /// One line per capability: its fixed identifier, its verdict, how many facts it measured,
    /// and how long it took. No path, folder, endpoint reason, device name, room member, or
    /// coordinate crosses into this. SupportBundle's schema is a closed projection for a reason
    /// and adding free text to it would undo that; this keeps the shape of the answer — which
    /// capability failed — without exporting the evidence.
    /// </remarks>
    public IReadOnlyList<(string Key, string Value)> ToSupportFacts(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return
        [
            ("self-test run", Capabilities.Count == 0 ? "never" : SupportWord(Outcome)),
            .. Capabilities.Select(capability => (
                capability.Id,
                string.Create(
                    culture,
                    $"{SupportWord(capability.Outcome)} · {Math.Min(capability.Facts.Count, 99)} fact(s) · {Math.Min(capability.Took.TotalSeconds, 999):0.0} s"))),
        ];
    }

    /// <summary>A duration a person would say out loud. "0.0 s" is not one.</summary>
    public static string Duration(TimeSpan took, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return took < TimeSpan.FromSeconds(1)
            ? SetupText.ProbeDurationMs(culture, took.TotalMilliseconds)
            : SetupText.ProbeDurationSeconds(culture, took.TotalSeconds);
    }

    public static string Word(SelfTestOutcome outcome) => outcome switch
    {
        SelfTestOutcome.Pass => SetupText.ProbeWordWorking,
        SelfTestOutcome.Fail => SetupText.ProbeWordNotWorking,
        SelfTestOutcome.Unknown => SetupText.ProbeWordUntestable,
        SelfTestOutcome.Running => SetupText.ProbeWordTesting,
        SelfTestOutcome.Waiting => SetupText.ProbeWordWaiting,
        _ => SetupText.ProbeWordNotTested,
    };

    /// <summary>The verdict words the outbound report carries, which stay English: its vocabulary is closed.</summary>
    private static string SupportWord(SelfTestOutcome outcome) => outcome switch
    {
        SelfTestOutcome.Pass => "working",
        SelfTestOutcome.Fail => "not working",
        SelfTestOutcome.Unknown => "could not be tested",
        SelfTestOutcome.Running => "testing",
        SelfTestOutcome.Waiting => "waiting for you",
        _ => "not tested",
    };
}
