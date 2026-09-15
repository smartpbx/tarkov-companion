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
        ArgumentNullException.ThrowIfNull(image);
        if (!MemoryMarshal.TryGetArray(image.Pixels, out var ownedPixels) || ownedPixels.Array is null)
        {
            throw new ArgumentException("Transient capture pixels must be backed by an owned byte array.", nameof(image));
        }

        _ownedPixels = ownedPixels;
        long requiredByteLength;
        long minimumStride;
        try
        {
            requiredByteLength = checked((long)image.Stride * image.Height);
            minimumStride = checked((long)image.Width * BytesPerPixel(image.Format));
        }
        catch (OverflowException)
        {
            CryptographicOperations.ZeroMemory(_ownedPixels.AsSpan());
            throw new ArgumentException("Transient capture dimensions overflow their pixel buffer.", nameof(image));
        }

        if (image.Width <= 0 || image.Height <= 0 || image.Stride <= 0
            || minimumStride <= 0 || image.Stride < minimumStride
            || requiredByteLength <= 0 || requiredByteLength != _ownedPixels.Count)
        {
            CryptographicOperations.ZeroMemory(_ownedPixels.AsSpan());
            throw new ArgumentException(
                "Transient capture dimensions must describe the entire owned pixel buffer exactly.",
                nameof(image));
        }

        ByteLength = requiredByteLength;
        _image = image;
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

        CryptographicOperations.ZeroMemory(_ownedPixels.AsSpan());
    }

    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        PixelFormat.Gray8 => 1,
        _ => 0,
    };
}
