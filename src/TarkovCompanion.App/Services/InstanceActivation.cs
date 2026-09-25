using System.Text;

namespace TarkovCompanion.App.Services;

/// <summary>
/// [#893] Lets a second launch hand itself to the copy already running: that copy comes to the
/// front, on the page the second launch asked for, and the second launch exits.
/// </summary>
/// <remarks>
/// <see cref="SingleInstance"/> stops a second copy starting, which is right, but the second copy
/// then exited with a message written to a log and to a console a windowed application does not
/// have. On the owner's PC (build 2.0.1437, 2026-09-23) the running window was behind the game;
/// the shortcut did nothing visible, so the running copy was killed and relaunched, which dropped
/// the relay and the raid and was reported on the next start as a run that died.
///
/// The channel is a named, per-session event the running copy waits on, and one small file beside
/// the logs that carries the page, if any. The file is written before the event is set, and read
/// and deleted by the running copy once it wakes, so a page is never lost to the order the two
/// arrive in. Nothing here starts a process, so the #599 launch rules are untouched.
///
/// Where the platform has no named events (a unit-test host on Linux) there is no channel: the
/// second launch falls back to saying where the window is.
/// </remarks>
public sealed class InstanceActivation : IDisposable
{
    public const string EventName = "Local\\TarkovCompanion.Activate";
    private const string RequestFileName = "activate.request";
    private const string NoPage = "-";

    private readonly EventWaitHandle _signal;
    private readonly string _requestPath;
    private readonly Action<string?> _activate;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private int _disposed;

    private InstanceActivation(EventWaitHandle signal, string requestPath, Action<string?> activate)
    {
        _signal = signal;
        _requestPath = requestPath;
        _activate = activate;
        _thread = new Thread(Listen) { IsBackground = true, Name = "Instance activation" };
        _thread.Start();
    }

    /// <summary>
    /// The running copy's side: waits for activations and calls <paramref name="activate"/> with the
    /// requested page (null for none) on a background thread. Null where no channel can be made.
    /// </summary>
    public static InstanceActivation? Listen(string directory, Action<string?> activate, Func<EventWaitHandle>? createSignal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(activate);
        try
        {
            var signal = createSignal?.Invoke() ?? new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            return new InstanceActivation(signal, Path.Combine(directory, RequestFileName), activate);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
                                          or UnauthorizedAccessException
                                          or IOException
                                          or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The second launch's side: asks the running copy to come forward (and open
    /// <paramref name="page"/>). False when there was nobody to ask or the asking failed.
    /// </summary>
    public static bool TrySignal(string directory, string? page, Func<EventWaitHandle?>? openSignal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            // A handle this method opened is closed here; one handed in belongs to its caller.
            using var opened = openSignal is null && OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(EventName, out var existing) ? existing : null;
            var signal = openSignal is not null ? openSignal() : opened;
            if (signal is null)
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, RequestFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, string.IsNullOrWhiteSpace(page) ? NoPage : page.Trim(), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            return signal.Set();
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
                                          or UnauthorizedAccessException
                                          or IOException
                                          or ObjectDisposedException
                                          or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _stop, _signal };
        while (WaitHandle.WaitAny(handles) == 1)
        {
            string? page = null;
            try
            {
                if (File.Exists(_requestPath))
                {
                    var text = File.ReadAllText(_requestPath, Encoding.UTF8).Trim();
                    File.Delete(_requestPath);
                    page = text.Length == 0 || text == NoPage ? null : text;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Coming to the front is the point; a page that could not be read is not.
            }

            try
            {
                _activate(page);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Diagnostics.CrashLog.Write("lifecycle", "Could not bring the window forward: " + exception.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
        _stop.Dispose();
    }
}
