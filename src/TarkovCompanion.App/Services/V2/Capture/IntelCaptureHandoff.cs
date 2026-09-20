using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>What one capture was read as, for the Intel workspace to open.</summary>
/// <param name="SessionId">The capture session, so the shell can correlate it with its review.</param>
/// <param name="ArtifactId">The frame, for the same reason.</param>
/// <param name="EffectiveIntent">What the capture was finally analyzed as.</param>
/// <param name="Best">The item Intel opens on.</param>
/// <param name="Alternates">The runners-up, in order, so a wrong answer can be corrected.</param>
/// <param name="ObservedUtc">When the frame was captured, never when this was published.</param>
/// <param name="DiagnosticCode">The recognizer's own code, unchanged.</param>
public sealed record CaptureItemIdentification(
    CaptureSessionId SessionId,
    string ArtifactId,
    ScanIntent EffectiveIntent,
    CaptureIdentifiedItem Best,
    IReadOnlyList<CaptureIdentifiedItem> Alternates,
    DateTimeOffset ObservedUtc,
    string? DiagnosticCode);

/// <summary>
/// The reviewed-result consumer for a capture whose answer is one item (#287).
/// </summary>
/// <remarks>
/// Loot and Stash captures each reach a workspace that knows what to do with a lattice. Everything
/// else — the item screen a player points Capture at to ask "what is this", and the ammo, key and
/// quest-item screens whose grids nothing reconstructs yet — was acknowledged and dropped: the
/// handoff returned Accepted and produced nothing, so "Understand this screen" understood nothing.
///
/// This publishes the identification the pipeline already made. It decides nothing and recognizes
/// nothing itself; it never claims more than the recognizer did, and a capture that identified no
/// item publishes nothing rather than opening Intel on a guess.
/// </remarks>
public sealed class IntelCaptureHandoff(ILogger<IntelCaptureHandoff>? logger = null) : ICaptureResultHandoff
{
    private readonly ILogger<IntelCaptureHandoff> _logger = logger ?? NullLogger<IntelCaptureHandoff>.Instance;

    /// <summary>Raised when a capture resolved to a catalog item. Never raised otherwise.</summary>
    public event EventHandler<CaptureItemIdentification>? ItemIdentified;

    /// <summary>The intents whose answer is an item rather than a lattice or a map.</summary>
    /// <remarks>
    /// Ammo, Keys and Quest items are here because their grid reconstruction does not exist yet
    /// (#273) and one identified item is a better answer than none. When those grids land, their
    /// own workspaces take the intent and this stops seeing it.
    /// </remarks>
    public static IReadOnlySet<ScanIntent> Intents { get; } = new HashSet<ScanIntent>
    {
        ScanIntent.Auto,
        ScanIntent.Ammo,
        ScanIntent.Keys,
        ScanIntent.QuestItems,
        ScanIntent.Flea,
    };

    public ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Intents.Contains(request.EffectiveIntent)
            ? AcceptItemAsync(request, cancellationToken)
            : ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    /// <summary>Publishes the item a frame was read as, whatever intent it arrived under.</summary>
    /// <remarks>
    /// An unarmed screenshot of one item's inspect screen is detected as an item, and intake
    /// turns a detected item into the Loot intent. The composite sends that here, since a frame
    /// with one named item and no lattice has nothing for the Loot Scan to decide.
    /// </remarks>
    public ValueTask<CaptureHandoffResult> AcceptItemAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Analysis.Identified.Count == 0)
        {
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }

        var identified = request.Analysis.Identified;
        _logger.LogInformation(
            "Capture {Artifact} read as {Item} with {Alternates} alternate(s).",
            request.ArtifactId,
            identified[0].CanonicalId,
            identified.Count - 1);
        ItemIdentified?.Invoke(this, new(
            request.SessionId,
            request.ArtifactId,
            request.EffectiveIntent,
            identified[0],
            [.. identified.Skip(1)],
            request.CapturedUtc,
            request.Analysis.DiagnosticCode));
        return ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }
}
