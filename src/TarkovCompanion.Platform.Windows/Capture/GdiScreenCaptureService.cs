using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Platform.Windows.Capture;

/// <summary>Captures visible pixels with GDI. Captured bytes remain in memory and are never persisted.</summary>
public sealed partial class GdiScreenCaptureService(
    IGameWindowLocator windowLocator,
    bool developerMode = false) : IScreenCaptureService
{
    public async Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GDI capture is available only on Windows.");
        }

        var window = await windowLocator.FindAsync(developerMode, cancellationToken).ConfigureAwait(false);
        if (window is not null && !window.IsMinimized)
        {
            return CaptureWindow(window, request.Region);
        }

        if (!request.AllowDesktopFallback)
        {
            throw new InvalidOperationException("The EFT window is unavailable and desktop fallback was not allowed.");
        }

        return CaptureDesktop(request.Region);
    }

    [SupportedOSPlatform("windows")]
    private static CapturedImage CaptureWindow(WindowDescriptor window, PixelRect? region)
    {
        var bounds = region ?? new PixelRect(0, 0, window.Bounds.Width, window.Bounds.Height);
        ValidateRegion(bounds, window.Bounds.Width, window.Bounds.Height);
        return Capture(window.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, $"window:{window.ProcessName}");
    }

    [SupportedOSPlatform("windows")]
    private static CapturedImage CaptureDesktop(PixelRect? region)
    {
        var desktop = new PixelRect(
            CaptureNative.GetSystemMetrics(76),
            CaptureNative.GetSystemMetrics(77),
            CaptureNative.GetSystemMetrics(78),
            CaptureNative.GetSystemMetrics(79));
        var bounds = region ?? desktop;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Windows reported an invalid virtual desktop size.");
        }

        return Capture(0, bounds.X, bounds.Y, bounds.Width, bounds.Height, "desktop-fallback");
    }

    [SupportedOSPlatform("windows")]
    private static CapturedImage Capture(nint window, int sourceX, int sourceY, int width, int height, string source)
    {
        var previousDpiContext = CaptureNative.SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var sourceDc = CaptureNative.GetWindowDC(window);
            if (sourceDc == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not acquire a capture device context.");
            }

            nint memoryDc = 0;
            nint bitmap = 0;
            nint previousObject = 0;
            try
            {
                memoryDc = CaptureNative.CreateCompatibleDC(sourceDc);
                bitmap = CaptureNative.CreateCompatibleBitmap(sourceDc, width, height);
                if (memoryDc == 0 || bitmap == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not allocate GDI capture resources.");
                }

                previousObject = CaptureNative.SelectObject(memoryDc, bitmap);
                const uint copyVisiblePixels = 0x00CC0020 | 0x40000000;
                if (CaptureNative.BitBlt(memoryDc, 0, 0, width, height, sourceDc, sourceX, sourceY, copyVisiblePixels) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI could not copy the visible pixels.");
                }

                var stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                    },
                };
                var buffer = Marshal.AllocHGlobal(pixels.Length);
                try
                {
                    if (CaptureNative.GetDIBits(memoryDc, bitmap, 0, (uint)height, buffer, ref info, 0) == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI could not read the captured bitmap.");
                    }

                    Marshal.Copy(buffer, pixels, 0, pixels.Length);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return new(pixels, width, height, stride, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, source);
            }
            finally
            {
                if (previousObject != 0 && memoryDc != 0)
                {
                    _ = CaptureNative.SelectObject(memoryDc, previousObject);
                }

                if (bitmap != 0)
                {
                    _ = CaptureNative.DeleteObject(bitmap);
                }

                if (memoryDc != 0)
                {
                    _ = CaptureNative.DeleteDC(memoryDc);
                }

                _ = CaptureNative.ReleaseDC(window, sourceDc);
            }
        }
        finally
        {
            if (previousDpiContext != 0)
            {
                _ = CaptureNative.SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }
    }

    private static void ValidateRegion(PixelRect region, int width, int height)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X + region.Width > width || region.Y + region.Height > height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Capture region must be contained within the selected window.");
        }
    }

    private static partial class CaptureNative
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint GetWindowDC(nint window);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(nint window, nint deviceContext);

        [LibraryImport("user32.dll")]
        internal static partial int GetSystemMetrics(int index);

        [LibraryImport("user32.dll")]
        internal static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateCompatibleDC(nint deviceContext);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

        [LibraryImport("gdi32.dll")]
        internal static partial nint SelectObject(nint deviceContext, nint graphicsObject);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial int BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial int GetDIBits(nint deviceContext, nint bitmap, uint start, uint lines, nint bits, ref BitmapInfo info, uint usage);

        [LibraryImport("gdi32.dll")]
        internal static partial int DeleteObject(nint graphicsObject);

        [LibraryImport("gdi32.dll")]
        internal static partial int DeleteDC(nint deviceContext);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Color;
    }
}
