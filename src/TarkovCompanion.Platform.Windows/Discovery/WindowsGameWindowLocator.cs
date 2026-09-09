using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Platform.Windows.Discovery;

public sealed record WindowCandidate(nint Handle, string ProcessName, string Title, PixelRect Bounds, bool IsMinimized);

public interface IWindowCatalog
{
    IReadOnlyList<WindowCandidate> GetWindows();
}

public sealed class WindowsGameWindowLocator(IWindowCatalog? catalog = null) : IGameWindowLocator
{
    private static readonly HashSet<string> GameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "EscapeFromTarkov",
        "EscapeFromTarkov_BE",
    };

    private readonly IWindowCatalog _catalog = catalog ?? new SystemWindowCatalog();

    public Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = _catalog.GetWindows()
            .Where(window => window.Handle != 0 && window.Bounds.Width > 0 && window.Bounds.Height > 0)
            .Select(window => new
            {
                Window = window,
                IsGame = GameProcesses.Contains(window.ProcessName),
                IsSimulator = IsSimulator(window),
            })
            .Where(item => item.IsGame || (developerMode && item.IsSimulator))
            .OrderByDescending(item => item.IsGame)
            .Select(item => new WindowDescriptor(
                item.Window.Handle,
                item.Window.ProcessName,
                item.Window.Title,
                item.Window.Bounds,
                item.Window.IsMinimized,
                item.IsSimulator))
            .FirstOrDefault();
        return Task.FromResult(candidate);
    }

    private static bool IsSimulator(WindowCandidate window) =>
        window.ProcessName.Equals("TarkovCompanion.EftSimulator", StringComparison.OrdinalIgnoreCase)
        || window.Title.Contains("Tarkov Companion EFT Simulator", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class SystemWindowCatalog : IWindowCatalog
{
    public IReadOnlyList<WindowCandidate> GetWindows() =>
        OperatingSystem.IsWindows() ? GetWindowsOnWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowCandidate> GetWindowsOnWindows()
    {
        _ = WindowNative.SetThreadDpiAwarenessContext(new nint(-4));
        var windows = new List<WindowCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle == 0 || WindowNative.GetWindowRect(handle, out var bounds) == 0)
                    {
                        continue;
                    }

                    windows.Add(new(
                        handle,
                        process.ProcessName,
                        process.MainWindowTitle,
                        new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                        WindowNative.IsIconic(handle) != 0));
                }
                catch (InvalidOperationException)
                {
                    // A process may exit while the ordinary window list is being read.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Some protected system processes deny metadata access and are unrelated to EFT.
                }
            }
        }

        return windows;
    }

    private static partial class WindowNative
    {
        [LibraryImport("user32.dll")]
        internal static partial int GetWindowRect(nint window, out NativeRect bounds);

        [LibraryImport("user32.dll")]
        internal static partial int IsIconic(nint window);

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
}
