using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>A captured screen the companion recognised and cannot read yet.</summary>
/// <param name="Title">What the screen was, and that it is not supported yet.</param>
/// <param name="Instead">What to do instead, in one line.</param>
public sealed record UnsupportedScreen(
    CaptureSessionId SessionId,
    string ArtifactId,
    CaptureCorrelationId CorrelationId,
    ScanIntent Intent,
    DateTimeOffset ObservedUtc,
    string Title,
    string Instead);

/// <summary>
/// The answer for the extract list, the map and the character screens: "not supported yet".
/// </summary>
/// <remarks>
/// #287. These fell through the composite handoff's default, which acknowledged them and produced
/// nothing: the player saw a progress bar and then silence, or, for the HEALTH tab (which draws
/// the stash beside the body), advice about a stash they had not asked about. This says what the
/// screen was, that nothing reads it yet, and where the thing they wanted lives instead.
/// </remarks>
public sealed class UnsupportedScreenHandoff : ICaptureResultHandoff
{
    public static IReadOnlySet<ScanIntent> Intents { get; } =
        new HashSet<ScanIntent> { ScanIntent.ExtractsAndMap, ScanIntent.HealthAndCharacter };

    public event EventHandler<UnsupportedScreen>? ScreenRead;

    public ValueTask<CaptureHandoffResult> AcceptAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (title, instead) = Describe(request.EffectiveIntent);
        ScreenRead?.Invoke(this, new(
            request.SessionId,
            request.ArtifactId,
            request.CorrelationId,
            request.EffectiveIntent,
            request.CapturedUtc,
            title,
            instead));
        return ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    public static (string Title, string Instead) Describe(ScanIntent intent) => intent switch
    {
        ScanIntent.HealthAndCharacter => (
            "Health screen · not supported yet",
            "For loot, capture the open container. For your kit, use Plan > Loadout."),
        ScanIntent.ExtractsAndMap => (
            "Extract list · not supported yet",
            "The Raid map already shows this map's extracts."),
        _ => ("Screen not supported yet", "Capture a container, your stash or a flea page."),
    };
}

/// <summary>What a scan result says about where its source pixels are.</summary>
/// <param name="Label">The chip.</param>
/// <param name="Detail">The tooltip and screen-reader text.</param>
public sealed record ScanRetentionLabel(string Label, string Detail);

/// <summary>
/// The retention chip on a scan result (#287): the companion saves no copy of the frame.
/// </summary>
/// <remarks>
/// Nothing in V1 or V2 reads a Debug Capture switch (see V2ShellText), so no frame is ever saved
/// by the companion and the chip never says "kept". A game-written screenshot is still in the
/// game's own folder, and Setup's tidy setting decides how long; that is said, because "not
/// kept" next to a file the player can open would read as a lie.
/// </remarks>
public static class ScanRetention
{
    public const string NotKept = "Image not kept";

    public static ScanRetentionLabel Describe(
        CaptureSourceKind source,
        ScreenshotRetentionSettings? tidy,
        bool heldInMemory)
    {
        var held = heldInMemory
            ? $" Held in memory for {ScanFrameMemory.HoldFor.TotalMinutes:0} minutes so Read as… can use it."
            : " Read as… needs a new capture.";
        if (source != CaptureSourceKind.GameWrittenScreenshot)
        {
            return new(NotKept, "No copy is saved." + held);
        }

        return tidy is { IsEnabled: true } on
            ? new(
                $"{NotKept} · game file tidied after {on.SafeRetentionHours} h",
                "No copy is saved. The game's own screenshot goes to the recycle bin after "
                    + $"{on.SafeRetentionHours} hours (Setup > Privacy)." + held)
            : new(
                $"{NotKept} · game file stays",
                "No copy is saved. The game's own screenshot stays in its folder." + held);
    }
}
