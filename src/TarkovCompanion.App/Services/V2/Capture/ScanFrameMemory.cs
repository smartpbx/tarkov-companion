using System.Security.Cryptography;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>The last analysed frame, held so "Read as…" can read it again.</summary>
/// <param name="ArtifactId">The capture the frame belongs to.</param>
/// <param name="ReadAs">The intent it was analysed under.</param>
/// <param name="Context">Where it was taken, so the second reading is taken in the same place.</param>
/// <param name="HeldUntilUtc">When it is wiped.</param>
public sealed record ScanFrameHold(
    string ArtifactId,
    ScanIntent ReadAs,
    CaptureContextMetadata Context,
    DateTimeOffset HeldUntilUtc);

/// <summary>
/// Keeps the one most recent captured frame in memory for a few minutes, never on disk.
/// </summary>
/// <remarks>
/// #287. The coordinator wipes a frame's pixels the moment analysis returns, so a result read as
/// the wrong thing could only be put right by taking the screenshot again, and the player has
/// usually closed that container by then. This wraps the pipeline, copies the frame before the
/// coordinator wipes it, and gives it back once per "Read as…".
///
/// One frame, not a history: a newer capture replaces it, it is zeroed when replaced or when
/// <see cref="HoldFor"/> runs out, and nothing is written anywhere. The page says "Image not kept"
/// and that stays true. A 3840x1080 frame is 16 MB, which is why it is one.
/// </remarks>
public sealed class ScanFrameMemory(
    ICaptureSessionPipeline inner,
    TimeProvider? timeProvider = null) : ICaptureSessionPipeline, IDisposable
{
    /// <summary>How long a frame is held after it was analysed.</summary>
    public static TimeSpan HoldFor { get; } = TimeSpan.FromMinutes(10);

    private readonly ICaptureSessionPipeline _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Held? _held;
    // #887: expiry used to run only when someone asked for the frame, so an 8-33 MB copy sat in
    // the heap for hours after the player left the review. The timer wipes it on time.
    private ITimer? _expiry;

    public async Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Copied before analysis: the coordinator zeroes the lease as soon as this returns.
        var copy = Copy(request.Image);
        var hold = new ScanFrameHold(
            request.ArtifactId,
            request.RequestedIntent,
            request.Context,
            _timeProvider.GetUtcNow().Add(HoldFor));
        Replace(new Held(hold, copy));
        return await _inner.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What is held for <paramref name="artifactId"/>, if it still is.</summary>
    public ScanFrameHold? Describe(string? artifactId)
    {
        lock (_gate)
        {
            ExpireUnsafe();
            return _held is { } held && string.Equals(held.Hold.ArtifactId, artifactId, StringComparison.Ordinal)
                ? held.Hold
                : null;
        }
    }

    /// <summary>A fresh copy of the held frame for <paramref name="artifactId"/>, owned by the caller.</summary>
    /// <remarks>A copy each time: whoever submits it hands it to a lease that zeroes it.</remarks>
    public CapturedImage? TakeCopy(string artifactId)
    {
        lock (_gate)
        {
            ExpireUnsafe();
            return _held is { } held && string.Equals(held.Hold.ArtifactId, artifactId, StringComparison.Ordinal)
                ? Copy(held.Image)
                : null;
        }
    }

    /// <summary>Whether a frame is resident, without the expiry check a read would run.</summary>
    internal bool HoldsFrame
    {
        get
        {
            lock (_gate)
            {
                return _held is not null;
            }
        }
    }

    public void Dispose() => Replace(null);

    private void Replace(Held? next)
    {
        Held? previous;
        ITimer? previousExpiry;
        lock (_gate)
        {
            previous = _held;
            previousExpiry = _expiry;
            _held = next;
            _expiry = next is null
                ? null
                : _timeProvider.CreateTimer(
                    static state => ((ScanFrameMemory)state!).Expire(),
                    this,
                    HoldFor,
                    Timeout.InfiniteTimeSpan);
        }

        previousExpiry?.Dispose();
        previous?.Wipe();
    }

    private void Expire()
    {
        lock (_gate)
        {
            ExpireUnsafe();
        }
    }

    private void ExpireUnsafe()
    {
        if (_held is { } held && held.Hold.HeldUntilUtc <= _timeProvider.GetUtcNow())
        {
            _held = null;
            _expiry?.Dispose();
            _expiry = null;
            held.Wipe();
        }
    }

    private static CapturedImage Copy(CapturedImage image) =>
        image with { Pixels = image.Pixels.ToArray() };

    private sealed record Held(ScanFrameHold Hold, CapturedImage Image)
    {
        public void Wipe()
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(Image.Pixels, out var owned))
            {
                CryptographicOperations.ZeroMemory(owned.AsSpan());
            }
        }
    }
}
