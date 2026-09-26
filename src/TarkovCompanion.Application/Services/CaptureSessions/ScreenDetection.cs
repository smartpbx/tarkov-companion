using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>Every game screen a screenshot can be of, as the detectors name them (#712 T2).</summary>
/// <remarks>
/// Trader, Hideout, GearTab, PostRaidSummary and Messenger have detectors that never claim a
/// frame yet: each needs a labelled real set before it may (epic #712, packages 1-4 to 1-10).
/// </remarks>
public enum ScreenKind
{
    Loot = 1,
    Stash,
    Item,
    Flea,
    Tasks,
    ExtractList,
    Health,

    /// <summary>The world view: nothing but the position the game wrote into the file name.</summary>
    Position,
    Trader,
    Hideout,
    GearTab,
    PostRaidSummary,
    Messenger,
}

/// <summary>What one frame showed, pixel-free, for every detector to score at once.</summary>
/// <param name="AnchorScores">Each OCR anchor context's score (0 where nothing was read).</param>
/// <param name="HealthTab">The HEALTH-tab classifier's confidence, or null when it did not fire.</param>
/// <param name="LootLatticeMeasured">A loot lattice was measured in the frame.</param>
/// <param name="StashLatticeMeasured">A stash lattice was measured in the frame.</param>
/// <param name="NameCarriesPosition">The game wrote a position into the file's name.</param>
public sealed record ScreenEvidence(
    IReadOnlyDictionary<ScanContext, double> AnchorScores,
    bool InRaid,
    double? HealthTab = null,
    bool LootLatticeMeasured = false,
    bool StashLatticeMeasured = false,
    int FleaRows = 0,
    bool NameCarriesPosition = false)
{
    public double Anchor(ScanContext context) =>
        AnchorScores.TryGetValue(context, out var score) ? Math.Clamp(score, 0, 1) : 0;
}

/// <summary>One detector's claim on a frame: how sure it is the frame is its screen, and why.</summary>
public sealed record ScreenVote(ScreenKind Kind, Confidence Confidence, string Reason);

/// <summary>One screen kind's detector. Every frame is offered to every detector.</summary>
public interface IScreenDetector
{
    ScreenKind Kind { get; }

    /// <summary>
    /// A fallback only wins when no screen detector is sure; it never makes a tie. The position in
    /// a file name is one: the game writes it into every in-raid screenshot, the loot ones too.
    /// </summary>
    bool IsFallback => false;

    ScreenVote Score(ScreenEvidence evidence);
}

public enum ScreenRoutingOutcome
{
    /// <summary>One detector was sure and nobody disagreed: its handler gets the frame.</summary>
    Routed = 1,

    /// <summary>Nobody was sure, or two were: the frame goes to the unrecognised tray.</summary>
    Unsure,

    /// <summary>Only a fallback claimed it (a world-view screenshot): nothing to show.</summary>
    Background,

    /// <summary>The player said what it was ("Read as…"): no detector was asked.</summary>
    Chosen,
}

/// <summary>Which detector won a frame, how sure it was, and the one line that says so.</summary>
public sealed record ScreenRouting(
    ScreenRoutingOutcome Outcome,
    ScreenKind? Kind,
    Confidence Confidence,
    ScreenVote? RunnerUp,
    IReadOnlyList<ScreenVote> Votes,
    string Because)
{
    public bool IsUnsure => Outcome == ScreenRoutingOutcome.Unsure;

    /// <summary>The best guesses, strongest first, for the tray's tooltip. Zero votes are left out.</summary>
    public IReadOnlyList<ScreenVote> Guesses => [.. Votes.Where(vote => vote.Confidence.Value > 0).Take(3)];

    public static ScreenRouting Chosen(ScreenKind kind) =>
        new(ScreenRoutingOutcome.Chosen, kind, Confidence.Certain, null, [], $"you chose Read as {ScreenKinds.Describe(kind)}");
}

/// <summary>
/// Offers a frame to every detector and picks the one that is sure (#712 T2, decision 9).
/// </summary>
/// <remarks>
/// <para>
/// The rules are the anchor detector's, applied across detectors instead of inside one: a
/// screen is placed at <see cref="Threshold"/> or above and only with <see cref="MinimumLead"/>
/// over the runner-up. The one rule added is that two detectors both at or above the threshold
/// is "could not tell", never the stronger of two confident claims: a wrong answer costs the
/// player more than no answer, and the tray lets them say what it was in one tap.
/// </para>
/// <para>
/// No threshold here is new. Both numbers are <c>ScanContextDetector</c>'s, which is where the
/// measured anchor weights were chosen; the lattice floor stays in the loot detector.
/// </para>
/// </remarks>
public sealed class ScreenDetectorRunner
{
    /// <summary>The anchor detector's placement threshold.</summary>
    public const double Threshold = 0.55;

    /// <summary>The anchor detector's runner-up lead.</summary>
    public const double MinimumLead = 0.10;

    private readonly IReadOnlyList<IScreenDetector> _detectors;

    public ScreenDetectorRunner(IEnumerable<IScreenDetector>? detectors = null)
    {
        _detectors = [.. detectors ?? ScreenDetectors.All];
        if (_detectors.Count == 0)
        {
            throw new ArgumentException("At least one screen detector is required.", nameof(detectors));
        }
    }

    public IReadOnlyList<IScreenDetector> Detectors => _detectors;

    public ScreenRouting Decide(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var scored = _detectors
            .Select(detector => (Detector: detector, Vote: detector.Score(evidence)))
            .ToArray();
        var votes = scored
            .Select(entry => entry.Vote)
            .OrderByDescending(vote => vote.Confidence.Value)
            .ThenBy(vote => vote.Kind)
            .ToArray();
        var screens = scored
            .Where(entry => !entry.Detector.IsFallback)
            .Select(entry => entry.Vote)
            .OrderByDescending(vote => vote.Confidence.Value)
            .ThenBy(vote => vote.Kind)
            .ToArray();
        var best = screens.FirstOrDefault();
        var runnerUp = screens.Skip(1).FirstOrDefault();
        if (best is not null && best.Confidence.Value >= Threshold)
        {
            var second = runnerUp?.Confidence.Value ?? 0;
            if (second >= Threshold)
            {
                return new(
                    ScreenRoutingOutcome.Unsure,
                    null,
                    best.Confidence,
                    runnerUp,
                    votes,
                    $"{Label(best)} and {Label(runnerUp!)} both claimed it");
            }

            if (best.Confidence.Value - second < MinimumLead)
            {
                return new(
                    ScreenRoutingOutcome.Unsure,
                    null,
                    best.Confidence,
                    runnerUp,
                    votes,
                    $"{Label(best)} was too close to {Label(runnerUp!)}");
            }

            return new(
                ScreenRoutingOutcome.Routed,
                best.Kind,
                best.Confidence,
                runnerUp,
                votes,
                $"{ScreenKinds.Describe(best.Kind)} detector, {Percent(best.Confidence)}: {best.Reason}");
        }

        var fallback = scored
            .Where(entry => entry.Detector.IsFallback && entry.Vote.Confidence.Value >= Threshold)
            .Select(entry => entry.Vote)
            .OrderByDescending(vote => vote.Confidence.Value)
            .FirstOrDefault();
        if (fallback is not null)
        {
            return new(
                ScreenRoutingOutcome.Background,
                fallback.Kind,
                fallback.Confidence,
                best,
                votes,
                fallback.Reason);
        }

        return new(
            ScreenRoutingOutcome.Unsure,
            null,
            best?.Confidence ?? Confidence.Unknown,
            runnerUp,
            votes,
            best is null || best.Confidence.Value <= 0
                ? "no detector recognised it"
                : $"best was {Label(best)}, below {Percent(new Confidence(Threshold))}");
    }

    private static string Label(ScreenVote vote) => $"{ScreenKinds.Describe(vote.Kind)} {Percent(vote.Confidence)}";

    private static string Percent(Confidence confidence) =>
        (confidence.Value * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

/// <summary>Plain names for the screen kinds, as a "because" line uses them.</summary>
public static class ScreenKinds
{
    public static string Describe(ScreenKind kind) => kind switch
    {
        ScreenKind.Loot => "loot",
        ScreenKind.Stash => "stash",
        ScreenKind.Item => "item",
        ScreenKind.Flea => "flea",
        ScreenKind.Tasks => "TASKS",
        ScreenKind.ExtractList => "extract list",
        ScreenKind.Health => "health",
        ScreenKind.Position => "position",
        ScreenKind.Trader => "trader",
        ScreenKind.Hideout => "hideout",
        ScreenKind.GearTab => "gear",
        ScreenKind.PostRaidSummary => "raid summary",
        ScreenKind.Messenger => "messenger",
        _ => kind.ToString(),
    };

    /// <summary>The anchor context a kind is filed under where the situation still speaks in those.</summary>
    public static ScanContext ToScanContext(ScreenKind kind) => kind switch
    {
        ScreenKind.Loot or ScreenKind.Stash => ScanContext.Container,
        ScreenKind.Item => ScanContext.SingleItem,
        ScreenKind.Flea => ScanContext.FleaListings,
        ScreenKind.Tasks => ScanContext.QuestTasks,
        ScreenKind.ExtractList => ScanContext.ExtractList,
        _ => ScanContext.Unknown,
    };
}

/// <summary>The detectors every frame is offered to, one per screen kind.</summary>
public static class ScreenDetectors
{
    public static IReadOnlyList<IScreenDetector> All { get; } =
    [
        new LootScreenDetector(),
        new StashScreenDetector(),
        new AnchorScreenDetector(ScreenKind.Item, ScanContext.SingleItem, "item-inspect words"),
        new AnchorScreenDetector(ScreenKind.Flea, ScanContext.FleaListings, "flea market words"),
        new AnchorScreenDetector(ScreenKind.Tasks, ScanContext.QuestTasks, "TASKS headings"),
        new AnchorScreenDetector(ScreenKind.ExtractList, ScanContext.ExtractList, "extract list words"),
        new HealthScreenDetector(),
        new PositionScreenDetector(),
        new NotReadYetScreenDetector(ScreenKind.Trader),
        new NotReadYetScreenDetector(ScreenKind.Hideout),
        new NotReadYetScreenDetector(ScreenKind.GearTab),
        new NotReadYetScreenDetector(ScreenKind.PostRaidSummary),
        new NotReadYetScreenDetector(ScreenKind.Messenger),
    ];
}

/// <summary>A screen told by its OCR anchors alone.</summary>
public sealed class AnchorScreenDetector(ScreenKind kind, ScanContext context, string words) : IScreenDetector
{
    public ScreenKind Kind { get; } = kind;

    public ScreenVote Score(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var score = evidence.Anchor(context);
        return new(Kind, new Confidence(score), score > 0 ? $"read {words}" : $"no {words}");
    }
}

/// <summary>An open container in raid: container words, lifted by a measured loot lattice.</summary>
/// <remarks>
/// The lift is #893's: a container already placed by its words, in raid, with its lattice
/// measured, is acted on at <see cref="RecognitionThresholds.Ambiguous"/> however weakly the words
/// were read. The HEALTH tab draws the stash beside the body and scores as a container at 1.0,
/// so a frame the health classifier claimed is not a container.
/// </remarks>
public sealed class LootScreenDetector : IScreenDetector
{
    public ScreenKind Kind => ScreenKind.Loot;

    public ScreenVote Score(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.HealthTab is not null)
        {
            return new(Kind, Confidence.Unknown, "the HEALTH tab draws a stash, not loot");
        }

        if (!evidence.InRaid)
        {
            return new(Kind, Confidence.Unknown, "not in a raid");
        }

        var score = evidence.Anchor(ScanContext.Container);
        if (score >= ScreenDetectorRunner.Threshold && evidence.LootLatticeMeasured)
        {
            return new(Kind, new Confidence(Math.Max(score, RecognitionThresholds.Ambiguous)), "container words and a measured grid, in raid");
        }

        return new(Kind, new Confidence(score), score > 0 ? "container words, in raid" : "no container words");
    }
}

/// <summary>The stash, between raids: container words and a measured stash lattice.</summary>
/// <remarks>
/// Out of raid a container screen may be a trader, a case or the character screen, all of which
/// draw container words; the stash lattice is what the stash panel alone has. No lift: nothing
/// measured the stash detector on real unarmed frames yet, so its words must carry it.
/// </remarks>
public sealed class StashScreenDetector : IScreenDetector
{
    public ScreenKind Kind => ScreenKind.Stash;

    public ScreenVote Score(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.HealthTab is not null)
        {
            return new(Kind, Confidence.Unknown, "the HEALTH tab is the character screen");
        }

        if (evidence.InRaid)
        {
            return new(Kind, Confidence.Unknown, "in a raid");
        }

        var score = evidence.Anchor(ScanContext.Container);
        return evidence.StashLatticeMeasured
            ? new(Kind, new Confidence(score), "container words and a measured stash grid")
            : new(Kind, Confidence.Unknown, score > 0 ? "container words but no stash grid" : "no container words");
    }
}

public sealed class HealthScreenDetector : IScreenDetector
{
    public ScreenKind Kind => ScreenKind.Health;

    public ScreenVote Score(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.HealthTab is { } confidence
            ? new(Kind, new Confidence(confidence), "limb captions and health values")
            : new(Kind, Confidence.Unknown, "no limb captions");
    }
}

/// <summary>The world view: the only thing known is the position in the file's name.</summary>
public sealed class PositionScreenDetector : IScreenDetector
{
    public ScreenKind Kind => ScreenKind.Position;

    public bool IsFallback => true;

    public ScreenVote Score(ScreenEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.NameCarriesPosition
            ? new(Kind, Confidence.Certain, "a position screenshot")
            : new(Kind, Confidence.Unknown, "no position in the name");
    }
}

/// <summary>A screen kind nothing reads yet. It never claims a frame, so it never misroutes one.</summary>
public sealed class NotReadYetScreenDetector(ScreenKind kind) : IScreenDetector
{
    public ScreenKind Kind { get; } = kind;

    public ScreenVote Score(ScreenEvidence evidence) => new(Kind, Confidence.Unknown, "not read yet");
}
