using System.Diagnostics;
using System.Text.Json;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.Infrastructure.Processes;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// The whole update with no window: check, fetch, apply, leave. <c>--apply-update-and-exit</c>.
/// </summary>
/// <remarks>
/// #599 could not have been found on Linux and was not found by Windows verification either,
/// because verification installed a build and never updated one. This is the seam for the step
/// that does: it runs the same gateway and the same hand-over as the button on Setup, against
/// whatever feed <c>TARKOV_UPDATE_FEED</c> names, and the step then requires the newer build in
/// <c>current\</c>.
///
/// Before it applies, it starts a child that outlives it and is given no working directory of
/// its own, which is what a browser opened from a link used to be. The child inherits this
/// process's working directory. When that was the install folder the updater could not move the
/// folder for as long as the child lived; the step starts this probe standing in the install
/// folder, as both shortcuts do, so the update only applies if the process really left it.
/// </remarks>
internal static class UpdateApplyProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(AppCommandLine options)
    {
        var report = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["workingDirectoryInsideInstall"] = OutsideInstallFolder.IsUnder(Environment.CurrentDirectory, AppContext.BaseDirectory),
        };
        try
        {
            // With the data folder, so an update keeps the running build's package and a rollback can find it.
            var gateway = new VelopackUpdateGateway(paths: AppDataPaths.Resolve());
            report["channel"] = gateway.Channel.Name;
            report["installed"] = gateway.InstalledBuild;
            if (!gateway.IsInstalled)
            {
                return Finish(options, report, 2, "not installed, so there is nothing to update");
            }

            if (options.RollBackAndExit)
            {
                var offer = gateway.FindPreviousAsync(CancellationToken.None).GetAwaiter().GetResult();
                report["previous"] = offer.Previous?.Version;
                report["previousSource"] = offer.Previous?.Source.ToString();
                if (offer.Previous is null)
                {
                    return Finish(options, report, 3, offer.Reason ?? "nothing older to go back to");
                }

                var back = gateway.DownloadPreviousAsync(offer, null, CancellationToken.None).GetAwaiter().GetResult();
                report["download"] = back.Status;
                if (!back.CanApply)
                {
                    return Finish(options, report, 4, "the older build was not accepted");
                }

                return HandOver(options, report, gateway);
            }

            var found = gateway.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            report["check"] = found.Status;
            if (!found.CanDownload)
            {
                return Finish(options, report, 3, "the feed offered nothing newer");
            }

            var fetched = gateway.DownloadAsync(CancellationToken.None).GetAwaiter().GetResult();
            report["download"] = fetched.Status;
            if (!fetched.CanApply)
            {
                return Finish(options, report, 4, "the download was not accepted");
            }

            return HandOver(options, report, gateway);
        }
        catch (Exception exception)
        {
            return Finish(options, report, 6, exception.Message);
        }
    }

    private static int HandOver(AppCommandLine options, SortedDictionary<string, object?> report, VelopackUpdateGateway gateway)
    {
        report["bystanderPid"] = StartBystander();
        Finish(options, report, 0, "handing over to the updater");

        static void Log(string line) => Console.Error.WriteLine(line);
        gateway.HandOver = new UpdateHandOver(
            new UpdateHandOverSteps(() => { }, () => { }, _ => Task.CompletedTask),
            new HardProcessEnder(Log),
            Log);
        gateway.ApplyAndRestart();
        return 5;
    }

    /// <summary>A process that does nothing for ninety seconds, started the careless way on purpose.</summary>
    private static int? StartBystander()
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("ping.exe", "-n 90 127.0.0.1")
                : new ProcessStartInfo("sleep", "90");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using var bystander = Process.Start(start);
            return bystander?.Id;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int Finish(AppCommandLine options, SortedDictionary<string, object?> report, int exitCode, string outcome)
    {
        report["outcome"] = outcome;
        report["exitCode"] = exitCode;
        var text = JsonSerializer.Serialize(report, Json);
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.Out.WriteLine(text);
        }
        else
        {
            var path = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return exitCode;
    }
}
