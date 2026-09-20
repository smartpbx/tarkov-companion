using SkiaSharp;
using Svg.Skia;
using System.Xml;
using System.Xml.Linq;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>
/// Turns one cached map drawing into one PNG.
/// </summary>
/// <remarks>
/// Public because it has two callers now, not one: the asset cache in this assembly, and the
/// application's own rasteriser child process, which exists so that a native fault inside Skia
/// takes a two-second child rather than the running companion. See
/// <see cref="OutOfProcessSvgRasterizer"/> for why that child exists at all.
/// </remarks>
public static class SvgMapRasterizer
{
    /// <summary>How large the rasterised map may be along its longer side.</summary>
    /// <remarks>
    /// A budget to render *up to*, not a ceiling to stay under. The distinction is the whole
    /// of this file's history: the scale used to be clamped at 1, which treated the SVG as
    /// though enlarging it would interpolate. It is a vector. Rendering it larger produces
    /// genuinely more detail, and rendering it at its intrinsic size produces whatever the
    /// author happened to type into the viewBox.
    ///
    /// For Factory that was 130.8 by 141.2 — a hundred and thirty pixels, stretched across the
    /// whole map panel, which is what "the factory map drawing is super low res" was.
    /// </remarks>
    private const int MaximumPreviewDimension = 4096;

    /// <summary>How much memory one preview's pixels may take.</summary>
    /// <remarks>
    /// 4096 x 4096 at four bytes a pixel is 64 MiB, so this cannot be exceeded by the dimension
    /// budget above on its own. It is here because the two numbers are independent: raising the
    /// dimension budget, or adding a colour type with more bytes per pixel, would otherwise
    /// silently quadruple what a single rasterisation asks the native allocator for. Skia does
    /// not raise a managed exception when an allocation inside a draw fails; it dereferences what
    /// it could not allocate, which arrives as an access violation.
    /// </remarks>
    private const long MaximumPreviewBytes = 64L * 1024 * 1024;

    /// <summary>How many elements the document may contain.</summary>
    /// <remarks>
    /// The upstream drawings are hand-authored and small — Reserve, one of the larger ones, is
    /// 89 KB with 228 paths across six layers. Four figures of slack over that is generous and
    /// still refuses a document built to exhaust the rasteriser. Validated before Skia sees it,
    /// because what Skia does with a document it cannot handle is not throw.
    /// </remarks>
    private const int MaximumElements = 200_000;

    /// <summary>How deeply the document may nest.</summary>
    /// <remarks>
    /// Playback of a recorded picture recurses, and a deeply nested document is the cheapest way
    /// to run a native stack out. The upstream maps nest a handful deep.
    /// </remarks>
    private const int MaximumDepth = 64;

    /// <summary>
    /// Only one rasterisation in this process at a time.
    /// </summary>
    /// <remarks>
    /// The per-asset gates in <see cref="TarkovDevMapAssetCache"/> keep one map's previews in
    /// order; they do nothing about two different maps, which is how three 64 MiB surfaces came
    /// to be allocated, drawn and PNG-encoded at once. Serialised here instead, at the one place
    /// that touches Skia, so no caller can reintroduce the overlap by adding a call site.
    ///
    /// The cost is latency on a cold multi-floor map and nothing at all afterwards, because the
    /// cache no longer rasterises a preview it already has.
    /// </remarks>
    private static readonly SemaphoreSlim Rasterizations = new(1, 1);
    private static int _drawing;
    private static int _mostDrawingAtOnce;

    /// <summary>The most rasterisations that have ever been inside Skia at once in this process.</summary>
    /// <remarks>
    /// One, if the gate above works. Counted inside the gate rather than inferred from it, so the
    /// test that guards the in-process fallback against running concurrently with itself (#452)
    /// fails if somebody adds a second way in.
    /// </remarks>
    internal static int MostDrawingAtOnce => Volatile.Read(ref _mostDrawingAtOnce);

    public static Task CreatePreviewAsync(
        string svgPath,
        string previewPath,
        CancellationToken cancellationToken) =>
        CreatePreviewAsync(svgPath, previewPath, visibleLayer: null, cancellationToken);

    public static async Task CreatePreviewAsync(
        string svgPath,
        string previewPath,
        string? visibleLayer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = LoadAndValidateSvg(svgPath);
        SelectVisibleLayer(document, visibleLayer);
        await Rasterizations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // On a worker, always. Every line below the await in this method used to run on
            // whichever thread called it, and all of the expensive part — parse, a 64 MiB
            // surface, the draw, the snapshot, the PNG encode — is above the first await. A
            // caller reaching this from the interface thread froze the window for the duration,
            // once per floor.
            var encoded = await Task.Run(
                    () =>
                    {
                        var drawing = Interlocked.Increment(ref _drawing);
                        try
                        {
                            int seen;
                            while (drawing > (seen = Volatile.Read(ref _mostDrawingAtOnce))
                                && Interlocked.CompareExchange(ref _mostDrawingAtOnce, drawing, seen) != seen)
                            {
                            }

                            return Render(document, cancellationToken);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _drawing);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                previewPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Rasterizations.Release();
        }
    }

    /// <summary>
    /// Draws the validated document and hands back the encoded PNG.
    /// </summary>
    /// <remarks>
    /// Everything Skia owns is created and released inside this one call, and nothing native
    /// outlives it. That is not tidiness: <see cref="SKSvg"/> owns the
    /// <see cref="SKPicture"/> it returns and disposes it with itself, and the instance used to
    /// be a bare local that was never read again after <c>Load</c> returned — unreachable, and
    /// therefore collectable, while the picture built from it was still being drawn. The fault
    /// reported on 2026-09-19 was an access violation inside <c>sk_canvas_draw_picture</c>,
    /// which is what drawing a picture whose native handle has been released looks like. Whether
    /// or not that was the cause on the day, it is not a race worth leaving open: the owner is
    /// disposed deterministically, after the draw, and kept alive until then.
    /// </remarks>
    private static byte[] Render(XDocument document, CancellationToken cancellationToken)
    {
        using var svgStream = new MemoryStream();
        document.Save(svgStream, SaveOptions.DisableFormatting);
        svgStream.Position = 0;
        using var svg = new SKSvg();
        var picture = svg.Load(svgStream)
            ?? throw new InvalidDataException("The cached SVG map could not be rendered.");
        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("The cached SVG map has invalid visual bounds.");
        }

        // Fills the budget in both directions rather than refusing to grow. Small maps gain
        // the most: Factory's viewBox is 130.8 x 141.2, so this is the difference between a
        // 131-pixel image and a 3795-pixel one, from the same file.
        var scale = Math.Min(
            MaximumPreviewDimension / (double)bounds.Width,
            MaximumPreviewDimension / (double)bounds.Height);
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, MaximumPreviewDimension);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, MaximumPreviewDimension);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixelBytes = (long)width * height * info.BytesPerPixel;
        if (pixelBytes > MaximumPreviewBytes)
        {
            throw new InvalidDataException(
                $"A {width} by {height} map preview needs more than the {MaximumPreviewBytes / (1024 * 1024)} MB allowed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("A map preview surface could not be created.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate(-bounds.Left, -bounds.Top);
        surface.Canvas.DrawPicture(picture);
        surface.Canvas.Flush();
        // The picture's owner must still be reachable here. Without this the only reference to
        // it is a local the compiler is free to treat as dead from the moment Load returned.
        GC.KeepAlive(svg);
        cancellationToken.ThrowIfCancellationRequested();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The SVG map preview could not be encoded.");
        return data.ToArray();
    }

    private static XDocument LoadAndValidateSvg(string svgPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 32L * 1024 * 1024,
        };
        using var reader = XmlReader.Create(svgPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The cached SVG map has no SVG root element.");
        }

        var externalReference = root.DescendantsAndSelf()
            .Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName is "href" or "src" &&
                !string.IsNullOrWhiteSpace(attribute.Value) &&
                !attribute.Value.StartsWith('#'));
        var externalStyle = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            .Any(element =>
                element.Value.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("file:", StringComparison.OrdinalIgnoreCase));
        if (externalReference is not null || externalStyle)
        {
            throw new InvalidDataException("The cached SVG map contains an external resource reference.");
        }

        ValidateShape(root);
        return document;
    }

    /// <summary>
    /// Refuses a document too large or too deep to hand to Skia, before Skia is handed it.
    /// </summary>
    /// <remarks>
    /// The check is here rather than downstream because downstream cannot refuse. An allocation
    /// Skia cannot satisfy during playback is not reported as an exception; a recursion it cannot
    /// finish is not reported at all. Both arrive as a native fault in a process that has already
    /// stopped running managed code.
    /// </remarks>
    private static void ValidateShape(XElement root)
    {
        var elements = 1;
        var depth = 0;
        foreach (var element in root.Descendants())
        {
            if (++elements > MaximumElements)
            {
                throw new InvalidDataException(
                    $"The cached SVG map has more than the {MaximumElements} elements allowed.");
            }

            var elementDepth = 0;
            for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            {
                elementDepth++;
            }

            depth = Math.Max(depth, elementDepth);
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException(
                    $"The cached SVG map nests deeper than the {MaximumDepth} levels allowed.");
            }
        }
    }

    private static void SelectVisibleLayer(XDocument document, string? visibleLayer)
    {
        if (string.IsNullOrWhiteSpace(visibleLayer))
        {
            return;
        }

        var layerGroups = document.Root!
            .Elements()
            .Where(element =>
                string.Equals(element.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .ToArray();
        if (!layerGroups.Any(group =>
                string.Equals(group.Attribute("id")?.Value, visibleLayer, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The cached SVG map does not contain upstream layer '{visibleLayer}'.");
        }

        foreach (var group in layerGroups)
        {
            var keepWithGroup = group.Attributes().FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "data-keep-with-group", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(group.Attribute("id")?.Value, visibleLayer, StringComparison.Ordinal) &&
                !string.Equals(keepWithGroup?.Value, visibleLayer, StringComparison.Ordinal))
            {
                group.Remove();
            }
        }
    }
}
