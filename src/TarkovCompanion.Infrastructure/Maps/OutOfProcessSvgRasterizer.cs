using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>
/// How to re-launch this application as a short-lived map rasteriser.
/// </summary>
/// <remarks>
/// Supplied by the composition root, which is the only place that knows whether the running
/// process is this application's own host executable. Under <c>dotnet run</c>, a test, or a tool,
/// <see cref="Environment.ProcessPath"/> is the muxer or the tool, and re-launching that with
/// these options would run something else entirely — so the composition root leaves it null and
/// rasterisation stays in process, exactly as it was.
/// </remarks>
/// <param name="FileName">The executable to run, normally this process's own.</param>
/// <param name="LeadingArguments">Arguments that must come before the rasteriser's own.</param>
/// <param name="Timeout">How long a child may take before it is killed and reported as failed.</param>
public sealed record SvgRasterizerHost(
    string FileName,
    IReadOnlyList<string> LeadingArguments,
    TimeSpan Timeout)
{
    /// <summary>The option naming the SVG to read. Its presence is what puts a process into rasteriser mode.</summary>
    public const string SvgOption = "--rasterise-svg";

    /// <summary>The option naming the PNG to write.</summary>
    public const string PreviewOption = "--rasterise-preview";

    /// <summary>The option naming which upstream layer group to keep, when only one floor is wanted.</summary>
    public const string LayerOption = "--rasterise-layer";

    /// <summary>The exit code a rasteriser child uses for a document it refused.</summary>
    public const int RefusedExitCode = 2;
}

/// <summary>
/// Thrown when the rasteriser child could not be started at all.
/// </summary>
/// <remarks>
/// Distinct from a child that ran and failed, and the distinction decides what happens next. A
/// child that could not be *started* says nothing about the document, so falling back to
/// rasterising in this process is right. A child that started and died says the opposite, and
/// retrying it here would reproduce the fault in the process that must survive.
/// </remarks>
public sealed class SvgRasterizerHostUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Rasterises a map drawing in a child process, so a native fault cannot take the application.
/// </summary>
/// <remarks>
/// The fault reported on 2026-09-19 was an access violation (0xc0000005) inside Skia at
/// <c>sk_canvas_draw_picture</c>, reached from the map asset cache. A native access violation
/// cannot be caught: it unwinds nothing, raises no managed exception, and the process is gone
/// before any handler could run. Bounding and validating what Skia is given, which
/// <see cref="SvgMapRasterizer"/> now does, makes it less likely; it cannot make it impossible,
/// because the remaining risk is in third-party native code.
///
/// A separate process can. The child does one rasterisation and exits; if it faults, the operating
/// system reports the fault as its exit code and the application reads that as "this drawing is
/// unavailable" — the same degraded state it already shows for a drawing that failed to download.
/// The map falls back to its photographic tiles where the variant has them, and to a plain
/// message where it does not.
///
/// The child is this same executable, so there is nothing extra to ship, no second native
/// dependency set, and no version skew possible between parent and child.
/// </remarks>
internal static class OutOfProcessSvgRasterizer
{
    /// <summary>The exit code Windows reports for a process killed by an access violation.</summary>
    /// <remarks>
    /// 0xC0000005 as a signed 32-bit integer. Named because the number on its own in a log entry
    /// is the single most useful fact about a crash of this kind and the least recognisable.
    /// </remarks>
    private const int AccessViolationExitCode = -1073741819;

    public static async Task CreatePreviewAsync(
        SvgRasterizerHost host,
        string svgPath,
        string previewPath,
        string? visibleLayer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(host.FileName)
        {
            // No shell, no window, and stderr read rather than inherited: the child is a
            // WinExe, so its own console output goes nowhere unless it is redirected here.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in host.LeadingArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(SvgRasterizerHost.SvgOption);
        start.ArgumentList.Add(svgPath);
        start.ArgumentList.Add(SvgRasterizerHost.PreviewOption);
        start.ArgumentList.Add(previewPath);
        if (!string.IsNullOrWhiteSpace(visibleLayer))
        {
            start.ArgumentList.Add(SvgRasterizerHost.LayerOption);
            start.ArgumentList.Add(visibleLayer);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new SvgRasterizerHostUnavailableException(
                    "The map rasteriser child process did not start.");
        }
        catch (Exception failure) when (failure is Win32Exception
                                        or FileNotFoundException
                                        or PlatformNotSupportedException
                                        or InvalidOperationException)
        {
            throw new SvgRasterizerHostUnavailableException(
                $"The map rasteriser child process could not be started from '{host.FileName}'.",
                failure);
        }

        using (process)
        {
            // Read while waiting. A child that filled a redirected pipe and was never drained
            // would block on its own write and then be killed by the deadline below, which reads
            // as a timeout and is really a deadlock.
            var errorText = process.StandardError.ReadToEndAsync(cancellationToken);
            var outputText = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(host.Timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                throw new InvalidDataException(
                    $"The map rasteriser took longer than {host.Timeout.TotalSeconds:0} seconds and was stopped.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            if (process.ExitCode == 0)
            {
                return;
            }

            var detail = (await errorText.ConfigureAwait(false)).Trim();
            if (detail.Length == 0)
            {
                detail = (await outputText.ConfigureAwait(false)).Trim();
            }

            // Reported as bad data rather than as a broken application, because that is what the
            // caller can act on: the asset cache already treats InvalidDataException as
            // "unavailable, say so and carry on".
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"The map rasteriser failed ({Describe(process.ExitCode)})"
                + $"{(detail.Length == 0 ? "." : $": {detail}")}"));
        }
    }

    private static string Describe(int exitCode) => exitCode switch
    {
        AccessViolationExitCode => "crashed with an access violation, 0xc0000005",
        SvgRasterizerHost.RefusedExitCode => "refused the drawing",
        _ => string.Create(CultureInfo.InvariantCulture, $"exit code {exitCode}"),
    };

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It exited between the check and the kill, or the platform will not let us. Either
            // way the caller is already being told the rasterisation failed.
        }
    }
}
