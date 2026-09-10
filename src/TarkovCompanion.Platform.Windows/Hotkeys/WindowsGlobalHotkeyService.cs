using System.ComponentModel;
using System.Runtime.InteropServices;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Platform.Windows.Hotkeys;

public sealed partial class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x5443;
    private const uint HotkeyMessage = 0x0312;
    private const uint QuitMessage = 0x0012;
    private readonly object _gate = new();
    private Thread? _thread;
    private TaskCompletionSource _stopped = CompletedSource();
    private uint _threadId;

    public event EventHandler? Pressed;

    public async Task RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RegisterHotKey is available only on Windows.");
        }

        TaskCompletionSource ready;
        lock (_gate)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("A global hotkey is already registered.");
            }

            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => RunMessagePump(gesture, ready))
            {
                IsBackground = true,
                Name = "TarkovCompanion.Hotkey",
            };
            _thread.Start();
        }

        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        Task stopped;
        uint threadId;
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            stopped = _stopped.Task;
            threadId = _threadId;
        }

        if (threadId != 0 && HotkeyNative.PostThreadMessage(threadId, QuitMessage, 0, 0) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop the hotkey message loop.");
        }

        await stopped.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the hotkey without letting a teardown failure escape.
    /// </summary>
    /// <remarks>
    /// This runs while the dependency-injection container is disposing its singletons. A
    /// throw here aborts that walk, so the services after it, including the open SQLite
    /// connections, are never disposed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await UnregisterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or EntryPointNotFoundException
                                          or ObjectDisposedException)
        {
        }
    }

    private void RunMessagePump(HotkeyGesture gesture, TaskCompletionSource ready)
    {
        try
        {
            _threadId = HotkeyNative.GetCurrentThreadId();
            _ = HotkeyNative.PeekMessage(out _, 0, 0, 0, 0);
            if (HotkeyNative.RegisterHotKey(0, HotkeyId, gesture.Modifiers, gesture.VirtualKey) == 0)
            {
                ready.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not register {gesture.DisplayName}."));
                return;
            }

            ready.TrySetResult();
            while (HotkeyNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == HotkeyMessage && message.WParam == HotkeyId)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _ = HotkeyNative.UnregisterHotKey(0, HotkeyId);
            lock (_gate)
            {
                _thread = null;
                _threadId = 0;
            }

            _stopped.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static partial class HotkeyNative
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial int RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

        [LibraryImport("user32.dll")]
        internal static partial int UnregisterHotKey(nint window, int id);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        internal static partial int PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);

        // user32 exports PostThreadMessageW/A, never a bare PostThreadMessage. LibraryImport
        // uses the name literally rather than appending a suffix the way DllImport could, so
        // without the explicit entry point every unregister threw EntryPointNotFoundException.
        // Nothing called this service until the scan shortcut was wired up, so it went unseen.
        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        internal static partial int PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }
}
