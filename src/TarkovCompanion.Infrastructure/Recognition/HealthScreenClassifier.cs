using System.Text.RegularExpressions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>What <see cref="HealthScreenClassifier"/> found on one frame.</summary>
/// <param name="IsHealthTab">Whether the frame is the character screen's HEALTH tab.</param>
/// <param name="Limbs">The limb captions read, in body order.</param>
/// <param name="LimbValues">How many "current/maximum" values were read beside them.</param>
/// <param name="Confidence">The share of the seven limbs read, when it is the HEALTH tab.</param>
public sealed record HealthScreenReading(
    bool IsHealthTab,
    IReadOnlyList<string> Limbs,
    int LimbValues,
    Confidence Confidence)
{
    public static HealthScreenReading None { get; } = new(false, [], 0, new(0));
}

/// <summary>
/// Tells the character screen's HEALTH tab from the screens that share its furniture.
/// </summary>
/// <remarks>
/// #287. The real HEALTH tab (2026-09-22, 3840x1080) draws the stash, pockets, backpack and pouch
/// on the right, so the context anchors score it a Container at 1.0: stash (0.65), backpack
/// (0.25), pockets (0.20). Armed as Loot or Stash it was measured as a grid and answered with
/// advice about somebody's stash; unarmed it was accepted as a grid and nothing was said. What
/// only this tab draws is the body: seven limb captions (HEAD, THORAX, STOMACH, LEFT/RIGHT ARM,
/// LEFT/RIGHT LEG), each over a "35/35" bar. The Gear tab labels its slots HEADWEAR, BODY ARMOR
/// and so on, never a bare limb, so four limbs and three values are the claim.
///
/// It says which screen this is and nothing about the body: limb values are not read into health
/// (docs/RECOGNITION.md, #305). Transcribed from the real frame, not measured on Windows OCR yet.
/// </remarks>
public static partial class HealthScreenClassifier
{
    private static readonly string[] LimbOrder =
        ["HEAD", "THORAX", "STOMACH", "LEFT ARM", "RIGHT ARM", "LEFT LEG", "RIGHT LEG"];

    public const int MinimumLimbs = 4;

    public const int MinimumValues = 3;

    public static HealthScreenReading Classify(OcrResult fullFrame)
    {
        ArgumentNullException.ThrowIfNull(fullFrame);
        var limbs = new HashSet<string>(StringComparer.Ordinal);
        var values = 0;
        foreach (var line in fullFrame.Lines)
        {
            var text = Spaces().Replace(line.Text ?? string.Empty, " ").Trim();
            if (LimbLine().Match(text) is { Success: true } limb)
            {
                limbs.Add(limb.Groups["limb"].Value.ToUpperInvariant());
                if (limb.Groups["value"].Success)
                {
                    values++;
                }
            }
            else if (ValueLine().IsMatch(text))
            {
                values++;
            }
        }

        var ordered = LimbOrder.Where(limbs.Contains).ToArray();
        var isHealthTab = ordered.Length >= MinimumLimbs && values >= MinimumValues;
        return new(
            isHealthTab,
            ordered,
            values,
            new Confidence(isHealthTab ? (double)ordered.Length / LimbOrder.Length : 0));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // A caption alone, or the engine joining it to its value on one line.
    [GeneratedRegex(
        @"^(?<limb>HEAD|THORAX|STOMACH|(?:LEFT|RIGHT) (?:ARM|LEG))(?: (?<value>\d{1,3} ?/ ?\d{1,3}))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimbLine();

    [GeneratedRegex(@"^\d{1,3} ?/ ?\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueLine();
}
