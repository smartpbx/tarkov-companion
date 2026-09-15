using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>Single-owner decoded pixels that are cleared when the capture leaves review.</summary>
public sealed class CapturePixelLease : IDisposable
{
    private CapturedImage? _image;
    private readonly ArraySegment<byte> _ownedPixels;

    public CapturePixelLease(CapturedImage image)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        if (!MemoryMarshal.TryGetArray(image.Pixels, out var ownedPixels) || ownedPixels.Array is null)
        {
            throw new ArgumentException("Transient capture pixels must be backed by an owned byte array.", nameof(image));
        }

        _ownedPixels = ownedPixels;

        if (image.Width <= 0 || image.Height <= 0 || image.Stride <= 0)
        {
            throw new ArgumentException("Transient capture dimensions must be positive.", nameof(image));
        }

        ByteLength = checked((long)image.Stride * image.Height);
        if (ByteLength > _ownedPixels.Count)
        {
            throw new ArgumentException("The pixel buffer is shorter than the declared image.", nameof(image));
        }
    }

    public CapturedImage Image => _image ?? throw new ObjectDisposedException(nameof(CapturePixelLease));

    public long ByteLength { get; }

    public bool IsDisposed => _image is null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _image, null) is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(
            _ownedPixels.Array!.AsSpan(_ownedPixels.Offset, checked((int)ByteLength)));
    }
}
