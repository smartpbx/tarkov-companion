using System.Diagnostics;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// Node runs <c>scripts/test-relay-crypto.mjs</c> against <c>Tablet/relay-crypto.js</c> for real,
/// the same way <see cref="TabletAcknowledgementScriptTests"/> runs
/// <c>scripts/test-command-acknowledgement.mjs</c>.
/// </summary>
/// <remarks>
/// #562: the script already pinned the relay-frame golden vectors and <c>desktopOnline</c>, but
/// nothing in the gate ever ran it — a porting bug in this file would have shipped silently. This
/// is also where <c>desktopStatusMessage</c> lives (#562's "The relay had a problem · retrying",
/// distinct from "the desktop has not been seen for Ns"): a real 5xx from the relay used to read
/// as the desktop itself being offline, which it was not.
/// </remarks>
public sealed class TabletRelayCryptoScriptTests
{
    [Fact]
    public async Task TheNodeChecksForTheGoldenVectorsAndTheConnectivityWordingPass()
    {
        var script = Path.Combine(RepositoryRoot(), "scripts", "test-relay-crypto.mjs");
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(process.ExitCode == 0, await output + await errors);
        Assert.Contains("All relay-crypto checks passed.", await output, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
