using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>
/// The rasteriser children that are running right now, so they can be ended with their parent.
/// </summary>
/// <remarks>
/// A child is this application's own executable, started from the install folder. When the parent
/// leaves through <see cref="Environment.Exit(int)"/> or is killed, nothing ends the child: it
/// goes on drawing for as long as its picture takes, holding <c>current\TarkovCompanion.exe</c>
/// open, and the updater cannot move a folder with an open file in it (#599). The updater is
/// meant to end such processes itself, but on the owner's machine it cannot open any process, so
/// it sees none. The parent still holds the handles it was given when it started them, which need
/// no permission to use.
/// </remarks>
public static class MapRasterizerChildren
{
    private static readonly ConcurrentDictionary<Process, byte> Running = new();

    /// <summary>How many children are being waited for.</summary>
    public static int Count => Running.Count;

    /// <summary>Remembers a child until the returned registration is disposed.</summary>
    public static IDisposable Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Running[process] = 0;
        return new Registration(process);
    }

    /// <summary>Ends every running child at once.</summary>
    /// <returns>How many were asked to stop.</returns>
    public static int KillAll()
    {
        var asked = 0;
        foreach (var process in Running.Keys)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    asked++;
                }
            }
            catch (Exception failure) when (failure is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Gone already, or already disposed by the caller that was waiting for it.
            }
        }

        return asked;
    }

    private sealed class Registration(Process process) : IDisposable
    {
        public void Dispose() => Running.TryRemove(process, out _);
    }
}
