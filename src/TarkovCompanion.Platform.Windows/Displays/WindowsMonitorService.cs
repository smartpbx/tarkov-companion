using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Platform.Windows.Displays;

public sealed partial class WindowsMonitorService : IMonitorService
{
    public Task<IReadOnlyList<DisplayDescriptor>> GetDisplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DisplayDescriptor> displays = OperatingSystem.IsWindows() ? EnumerateOnWindows() : [];
        return Task.FromResult(displays);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DisplayDescriptor> EnumerateOnWindows()
    {
        _ = MonitorNative.SetThreadDpiAwarenessContext(new nint(-4));
        var displays = new List<DisplayDescriptor>();
        MonitorNative.MonitorEnumeration callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (MonitorNative.GetMonitorInfo(monitor, ref info) == 0)
            {
                return 1;
            }

            var scale = 1d;
            if (MonitorNative.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            {
                scale = dpiX / 96d;
            }

            var number = displays.Count + 1;
            displays.Add(new(
                $"monitor-{monitor.ToInt64():X}",
                $"Display {number}",
                new PixelRect(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top),
                (info.Flags & 1) != 0,
                scale));
            return 1;
        };

        if (MonitorNative.EnumDisplayMonitors(0, 0, callback, 0) == 0)
        {
            return [];
        }

        return displays;
    }

    private static partial class MonitorNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int MonitorEnumeration(nint monitor, nint deviceContext, nint bounds, nint data);

        [LibraryImport("user32.dll")]
        internal static partial int EnumDisplayMonitors(nint deviceContext, nint clip, MonitorEnumeration callback, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        internal static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [LibraryImport("shcore.dll")]
        internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

        [LibraryImport("user32.dll")]
        internal static partial nint SetThreadDpiAwarenessContext(nint dpiContext);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }
}
