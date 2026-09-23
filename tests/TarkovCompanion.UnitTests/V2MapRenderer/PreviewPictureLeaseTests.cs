using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#775] A map preview over the Raid cockpit's picture never shows a picture the cockpit freed.
/// </summary>
/// <remarks>
/// The cockpit replaces its picture while tiles fill in and frees the old one after the render
/// pass (a Background post, as <c>RaidCockpitViewModel.ReleasePicture</c> does). Plan's preview
/// was not re-presented for it, so it drew the freed picture: the "disposed bitmap" the render
/// tool hit on the Plan route. The cockpit side is modelled here with the same PictureLeases and
/// the same posted dispose; the preview is the real renderer view model.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PreviewPictureLeaseTests
{
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);

    [Fact]
    public async Task A_preview_not_re_presented_keeps_its_picture_alive_until_it_moves_off_it()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(MapMarkClipTests.MarkClipApp));
        await session.Dispatch(
            () =>
            {
                var cockpit = new Cockpit();
                var first = cockpit.Replace(ShaA);
                var preview = new MapSceneRendererViewModel(
                    Scene(ShaA),
                    MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
                    reviewedAssetResolver: cockpit.Resolve,
                    showsDetailsPanel: false,
                    pictureLease: cockpit.Lease);
                Assert.Same(first, preview.BackgroundImage);

                // Tiles arrive: the cockpit swaps its picture and retires the old one. The preview
                // is not told (Plan's signature did not change), and the render pass ends.
                cockpit.Replace(ShaB);
                Dispatcher.UIThread.RunJobs();

                Assert.Same(first, preview.BackgroundImage);
                Assert.DoesNotContain(first, cockpit.Freed);

                // Re-presented for the new picture: it moves on, and only then is the old one freed.
                preview.Present(Scene(ShaB));
                Assert.Same(cockpit.Current, preview.BackgroundImage);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(first, cockpit.Freed);
                Assert.Equal(1, preview.HeldPictureCount);

                // Dropped by its host: the current picture stays the cockpit's, and is freed with it.
                var second = cockpit.Current!;
                preview.ReleasePictures();
                Assert.Null(preview.BackgroundImage);
                Assert.Equal(0, preview.HeldPictureCount);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(second, cockpit.Freed);
                cockpit.Replace(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(second, cockpit.Freed);
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_picture_already_retired_is_not_shown()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(MapMarkClipTests.MarkClipApp));
        await session.Dispatch(
            () =>
            {
                var cockpit = new Cockpit();
                var stale = cockpit.Replace(ShaA);
                cockpit.Replace(ShaB);

                // A resolver that still hands out the replaced picture (a scene built from assets a
                // moment old) is refused a lease, so the preview draws nothing rather than it.
                var preview = new MapSceneRendererViewModel(
                    Scene(ShaA),
                    MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
                    reviewedAssetResolver: _ => stale,
                    showsDetailsPanel: false,
                    pictureLease: cockpit.Lease);

                Assert.Null(preview.BackgroundImage);
                Assert.Equal(0, preview.HeldPictureCount);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(stale, cockpit.Freed);
            },
            CancellationToken.None);
    }

    /// <summary>The cockpit's picture ownership: tracked on creation, retired on replacement, freed after the render pass.</summary>
    private sealed class Cockpit
    {
        private readonly PictureLeases<Bitmap> _pictures;
        private string? _sha;

        public Cockpit() =>
            _pictures = new(picture => Dispatcher.UIThread.Post(
                () =>
                {
                    Freed.Add(picture);
                    picture.Dispose();
                },
                DispatcherPriority.Background));

        public List<Bitmap> Freed { get; } = [];

        public Bitmap? Current { get; private set; }

        public Bitmap? Replace(string? sha)
        {
            var previous = Current;
            Current = sha is null ? null : new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _sha = sha;
            if (Current is not null)
            {
                _pictures.Track(Current);
            }

            if (previous is not null)
            {
                _pictures.Retire(previous);
            }

            return Current;
        }

        public IImage? Resolve(MapSceneAsset asset) =>
            string.Equals(asset.ContentSha256, _sha, StringComparison.Ordinal) ? Current : null;

        public IDisposable? Lease(IImage image) => _pictures.TryRead((Bitmap)image);
    }

    private static MapSceneSnapshot Scene(string sha) => new(
        1,
        "customs",
        "customs",
        "customs",
        new MapSceneBounds(0, 0, 400, 300),
        [],
        new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
        new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), []),
        [],
        [],
        [
            new MapSceneAsset(
                new($"asset:customs:{sha[0]}"),
                MapSceneAssetKind.Background2D,
                new Uri("https://example.test/map.png"),
                new Uri("https://example.test/licence"),
                sha,
                "fixture",
                "customs",
                "current",
                MapSceneAssetReviewStatus.Reviewed,
                new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero)),
        ]);
}
