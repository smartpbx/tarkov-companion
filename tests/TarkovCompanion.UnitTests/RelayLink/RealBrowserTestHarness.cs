using System.Diagnostics;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// Real browsers are outside the test host, so an assertion timeout alone cannot reap Chromium.
/// This base keeps every launched process under the test lifetime and kills its whole tree when
/// xUnit disposes a failed or timed-out test class.
/// </summary>
public abstract class RealBrowserTestHarness : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Process> _browserProcesses = [];

    protected Process StartBrowser(ProcessStartInfo startInfo)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("TARKOV_REAL_BROWSER_FORCE_HANG"),
                "1",
                StringComparison.Ordinal))
        {
            startInfo = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("setInterval(() => {}, 1000)");
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("node did not start.");
        lock (_gate)
        {
            _browserProcesses.Add(process);
        }

        return process;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var process in _browserProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The test's using declaration already disposed this process.
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>A real-browser test must never hold a build slot indefinitely.</summary>
/// <remarks>
/// Three minutes, not one. The pairing waits inside the scripts are 45 s each because Chromium
/// starting beside the rest of the suite on a loaded runner has taken longer than the old 15 s,
/// and a ceiling below their sum turned a slow start into a timeout of its own.
/// </remarks>
internal sealed class RealBrowserFactAttribute : FactAttribute
{
    public RealBrowserFactAttribute() => Timeout = 180_000;
}
