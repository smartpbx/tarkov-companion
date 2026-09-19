using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// This executable's second job: drawing one map picture for the copy of itself that asked.
/// </summary>
/// <remarks>
/// Rasterising a map drawing killed the application on 2026-09-19 with a native access violation
/// inside Skia (0xc0000005, at <c>sk_canvas_draw_picture</c>). A native fault raises no managed
/// exception and unwinds nothing, so no amount of care in the calling code can catch it; the only
/// arrangement that survives one is for the drawing to happen in a process the application can
/// afford to lose. <see cref="OutOfProcessSvgRasterizer"/> is the caller; this is the callee.
///
/// The child is this same executable rather than a separate tool, so there is nothing extra to
/// ship, no second copy of the Skia native assets to keep aligned, and no way for parent and child
/// to disagree about a version.
///
/// It is deliberately the very first thing <c>Program.Main</c> does. A Velopack install hook, the
/// single-instance guard, the crash log's own installation and a window are all correct for an
/// application starting and all wrong for a process that exists to draw one picture and exit.
/// </remarks>
internal static class MapRasterizerHost
{
    /// <summary>
    /// Draws the requested picture, or reports that these arguments were not asking for one.
    /// </summary>
    /// <returns>An exit code when this process is a rasteriser, and null when it is not.</returns>
    public static int? TryRun(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Value(args, SvgRasterizerHost.SvgOption) is not { Length: > 0 } svgPath)
        {
            return null;
        }

        if (Value(args, SvgRasterizerHost.PreviewOption) is not { Length: > 0 } previewPath)
        {
            Console.Error.WriteLine($"{SvgRasterizerHost.SvgOption} requires {SvgRasterizerHost.PreviewOption} <png>.");
            return SvgRasterizerHost.RefusedExitCode;
        }

        try
        {
            SvgMapRasterizer
                .CreatePreviewAsync(svgPath, previewPath, Value(args, SvgRasterizerHost.LayerOption), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            // Everything, including what would ordinarily be a defect. The parent reads stderr and
            // the exit code and shows the player that this drawing is unavailable; a stack trace
            // written here is more use than one thrown in a process with no window and no log.
            Console.Error.WriteLine(exception.ToString());
            return SvgRasterizerHost.RefusedExitCode;
        }
    }

    private static string? Value(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal))
            {
                continue;
            }

            return index + 1 < args.Length ? args[index + 1] : null;
        }

        return null;
    }
}
