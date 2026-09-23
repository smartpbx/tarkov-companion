using Avalonia.Media;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// The leases one map renderer holds on the pictures it is showing, so its owner cannot free a
/// picture that is still bound to the screen.
/// </summary>
/// <remarks>
/// [#775] Plan's objective map and Team's marks map are renderers of their own over the Raid
/// cockpit's picture. The cockpit replaces that picture about twice a second while tiles fill in
/// and frees the old one after the current render pass. Plan re-presented its map only when the
/// objectives changed, so it kept drawing the freed picture: "disposed bitmap" on the interface
/// thread. The cockpit's own renderer had a smaller gap of the same kind, a rebuild that replaced
/// the picture and then awaited floor artwork, or was cancelled, before presenting.
///
/// A renderer now takes a lease (<c>PictureLeases.TryRead</c>) on each picture as it resolves
/// it, and ends the lease once the picture is no longer on it. A retired picture is refused, and
/// the renderer shows no picture rather than a freed one.
/// </remarks>
internal sealed class RendererPictureHold(Func<IImage, IDisposable?> lease)
{
    private readonly Dictionary<IImage, IDisposable> _held = new(ReferenceEqualityComparer.Instance);

    /// <summary>How many pictures are held.</summary>
    public int Count => _held.Count;

    /// <summary>The picture, now held; or null when its owner has already retired it.</summary>
    public IImage? Hold(IImage? image)
    {
        if (image is null || _held.ContainsKey(image))
        {
            return image;
        }

        if (lease(image) is not { } taken)
        {
            return null;
        }

        _held.Add(image, taken);
        return image;
    }

    /// <summary>Ends the lease on every held picture that is not in <paramref name="inUse"/>.</summary>
    public void Keep(IEnumerable<IImage?> inUse)
    {
        if (_held.Count == 0)
        {
            return;
        }

        var keep = new HashSet<IImage>(inUse.OfType<IImage>(), ReferenceEqualityComparer.Instance);
        foreach (var (image, taken) in _held.Where(pair => !keep.Contains(pair.Key)).ToArray())
        {
            _held.Remove(image);
            taken.Dispose();
        }
    }

    /// <summary>Ends every lease.</summary>
    public void ReleaseAll() => Keep([]);
}
