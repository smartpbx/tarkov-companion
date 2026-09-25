using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// [#893] One decode of one settled screenshot, shared by the readers that each want its pixels.
/// </summary>
/// <remarks>
/// Every in-raid screenshot is read twice: by the capture session (the V2 capture panel, the
/// Loot Scan) and by the always-on scan (the Raid HUD, the extract list, quest bursts). Each used
/// to decode the PNG itself, a 3840x1080 frame decoded twice per screenshot on the player's PC
/// while the game ran. The OCR was already shared by pixel hash (#453, OcrCoordinator); the
/// decode was not.
///
/// Each reader still gets a buffer of its own, because the capture session zeroes its pixels
/// when it is done with them (CapturePixelLease), and a shared array would be wiped under the
/// other reader. The first readers get a copy; the last expected reader takes the decoded
/// buffer itself, so at most two buffers are alive, as before, and the file is decoded once.
///
/// A decode that failed is not remembered: the capture session retries a file that was still
/// being written, and that retry has to decode again. A reader beyond the expected count, or one
/// that arrives after the buffer was handed over, decodes for itself.
/// </remarks>
public sealed class SharedScreenshotDecode : IScreenshotImageLoader
{
    private readonly IScreenshotImageLoader _inner;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CapturedImage? _decoded;
    private int _expectedReaders;

    public SharedScreenshotDecode(IScreenshotImageLoader inner, string path, int expectedReaders)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A path is required.", nameof(path)) : path;
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedReaders, 1);
        _expectedReaders = expectedReaders;
    }

    /// <summary>How many times the file itself was decoded, for diagnostics and tests.</summary>
    public int Decodes { get; private set; }

    public async Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!string.Equals(path, _path, StringComparison.Ordinal))
        {
            return await _inner.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_expectedReaders <= 0)
            {
                Decodes++;
                return await _inner.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            }

            if (_decoded is null)
            {
                Decodes++;
                _decoded = await _inner.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (_decoded is null)
                {
                    return null;
                }
            }

            return Take();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One expected reader will not come (its intake refused the frame); the buffer is handed over
    /// or let go sooner.
    /// </summary>
    public void Release()
    {
        _gate.Wait();
        try
        {
            if (_expectedReaders <= 0)
            {
                return;
            }

            _expectedReaders--;
            if (_expectedReaders == 0)
            {
                _decoded = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private CapturedImage Take()
    {
        var decoded = _decoded!;
        _expectedReaders--;
        if (_expectedReaders == 0)
        {
            _decoded = null;
            return decoded;
        }

        return decoded with { Pixels = decoded.Pixels.ToArray() };
    }
}
