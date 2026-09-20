using System.Globalization;
using System.Text;

namespace TarkovCompanion.App.Services.V2.Team;

/// <summary>
/// Draws a <see cref="QrCode"/> as path data a <c>Path</c> can fill.
/// </summary>
/// <remarks>
/// A path rather than a bitmap: the symbol is then resolution-independent, so it stays crisp at
/// any window scale and at any text scale, and it needs no image encoder in the application.
///
/// A string rather than a <c>Geometry</c>, because building a <c>StreamGeometry</c> needs a
/// platform render interface and a view model that cannot be constructed without a windowing
/// platform cannot be unit tested. The view's <c>Data</c> binding converts it.
///
/// The coordinate space is one unit per module, quiet zone included, so a caller sizes it by
/// giving the Path a width and letting it stretch. The quiet zone is part of the path because a
/// QR without one is a QR a reader may refuse, and leaving it to the layout is how it gets
/// forgotten. One trailing move keeps the empty margin inside the bounds.
/// </remarks>
public static class QrGeometry
{
    /// <summary>The specification's minimum light margin, in modules.</summary>
    public const int QuietZoneModules = 4;

    /// <summary>The dark modules of <paramref name="code"/> as one filled path.</summary>
    public static string PathData(QrCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var extent = Extent(code);
        var path = new StringBuilder();
        for (var row = 0; row < code.Size; row++)
        {
            var column = 0;
            while (column < code.Size)
            {
                if (!code[row, column])
                {
                    column++;
                    continue;
                }

                // One rectangle per run of dark modules in a row, not one per module: a row of
                // twenty dark modules is one figure rather than twenty, which keeps a version 8
                // symbol's path a few kilobytes instead of tens.
                var start = column;
                while (column < code.Size && code[row, column])
                {
                    column++;
                }

                var x = start + QuietZoneModules;
                var y = row + QuietZoneModules;
                var width = column - start;
                path.Append(CultureInfo.InvariantCulture, $"M{x},{y}h{width}v1h-{width}z");
            }
        }

        // The quiet zone has nothing in it, so without this the path's bounds would stop at the
        // last dark module and a stretched Path would scale the margin away.
        path.Append(CultureInfo.InvariantCulture, $"M0,0M{extent},{extent}");
        return path.ToString();
    }

    /// <summary>The geometry's width and height in modules, quiet zone included.</summary>
    public static int Extent(QrCode code) =>
        (code ?? throw new ArgumentNullException(nameof(code))).Size + (QuietZoneModules * 2);
}
