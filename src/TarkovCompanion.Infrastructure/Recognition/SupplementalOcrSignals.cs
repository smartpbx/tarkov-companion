using System.Text.RegularExpressions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public enum SupplementalOcrSignalKind
{
    HealthAndCharacter,
    VersionStrip,
}

/// <summary>
/// A position-independent text signal from the full frame. Confidence is the weakest provider
/// score among the evidence lines, or null when any evidence line came from an unscored engine.
/// It is not a recognition benchmark score.
/// </summary>
public sealed record SupplementalOcrSignal(
    SupplementalOcrSignalKind Kind,
    bool IsPresent,
    PixelRect? Bounds,
    Confidence? Confidence,
    IReadOnlyList<OcrLine> EvidenceLines,
    string DiagnosticCode);

public sealed record SupplementalOcrSignals(
    SupplementalOcrSignal HealthAndCharacter,
    SupplementalOcrSignal VersionStrip);

/// <summary>
/// Finds character/health chrome and the game-version strip from all OCR lines, without a
/// location crop. Those surfaces move with window shape and interface scale; position is
/// evidence returned after a match, never a prerequisite that can silently exclude it.
/// </summary>
public sealed partial class SupplementalOcrSignalDetector
{
    private static readonly string[] CharacterTerms =
    [
        "overall",
        "customization",
        "achievements",
        "health",
        "skills",
        "map",
        "tasks",
        "gear",
        "back",
        "search",
    ];

    private readonly OcrTextNormalizer _normalizer;

    public SupplementalOcrSignalDetector(OcrTextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public SupplementalOcrSignals Detect(OcrResult fullFrame)
    {
        ArgumentNullException.ThrowIfNull(fullFrame);
        // Matched word by word on short lines. The tab bar is one row of captions, and whether
        // the engine returns it as seven lines or one depends on the spacing it measures: two
        // real Gear screens 14 seconds apart (2026-09-20) came back present and absent when a
        // whole line had to equal "health". A line longer than a caption row is prose, where
        // "health" and "map" can both occur in an item description.
        var characterLines = fullFrame.Lines
            .Select(line => (Line: line, Terms: CharacterTermsIn(line.Text)))
            .Where(entry => entry.Terms.Count > 0)
            .OrderBy(entry => entry.Line.Bounds.Y)
            .ThenBy(entry => entry.Line.Bounds.X)
            .ToArray();
        var terms = characterLines.SelectMany(entry => entry.Terms).ToHashSet(StringComparer.Ordinal);
        // HEALTH plus one independent character-menu caption is the smallest useful
        // structural claim. A lone word "health" can occur in a tooltip or item description.
        var characterPresent = terms.Contains("health") && terms.Count >= 2;

        var versionLines = fullFrame.Lines
            .Where(line => VersionPattern().IsMatch(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();

        return new(
            Signal(
                SupplementalOcrSignalKind.HealthAndCharacter,
                characterPresent,
                characterPresent ? characterLines.Select(entry => entry.Line).ToArray() : [],
                characterPresent ? "health_character_text_present" : "health_character_text_absent"),
            Signal(
                SupplementalOcrSignalKind.VersionStrip,
                versionLines.Length > 0,
                versionLines,
                versionLines.Length > 0 ? "version_strip_text_present" : "version_strip_text_absent"));
    }

    /// <summary>Longest line, in words, still read as a row of captions.</summary>
    private const int MaximumCaptionRowWords = 8;

    private IReadOnlyList<string> CharacterTermsIn(string text)
    {
        var words = _normalizer.NormalizeForLookup(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is 0 or > MaximumCaptionRowWords
            ? []
            : words.Where(word => CharacterTerms.Contains(word, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static SupplementalOcrSignal Signal(
        SupplementalOcrSignalKind kind,
        bool present,
        IReadOnlyList<OcrLine> lines,
        string diagnostic)
    {
        var evidence = lines.ToArray();
        Confidence? confidence = evidence.Length > 0 && evidence.All(line => line.Confidence is not null)
            ? new Confidence(evidence.Min(line => line.Confidence!.Value.Value))
            : null;
        return new(
            kind,
            present,
            evidence.Length == 0 ? null : Union(evidence.Select(line => line.Bounds)),
            confidence,
            evidence,
            diagnostic);
    }

    private static PixelRect Union(IEnumerable<PixelRect> rectangles)
    {
        var bounds = rectangles.ToArray();
        var left = bounds.Min(rectangle => rectangle.X);
        var top = bounds.Min(rectangle => rectangle.Y);
        var right = bounds.Max(rectangle => checked(rectangle.X + rectangle.Width));
        var bottom = bounds.Max(rectangle => checked(rectangle.Y + rectangle.Height));
        return new(left, top, right - left, bottom - top);
    }

    [GeneratedRegex(@"(?<!\d)\d{1,3}(?:\.\d{1,6}){3,4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
