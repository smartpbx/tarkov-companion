using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.StashScan;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// #290: hands the desktop's last flea screen and Stash scan to the paired tablet as they are shown.
/// </summary>
/// <remarks>
/// The Stash page loads a snapshot when it is opened, and a capture read as the stash opens it, so
/// the page's own <c>Items</c> changing is the moment a scan is ready to review: the tablet gets
/// the same rows the page just drew. An older snapshot picked on the desktop is not sent; the
/// tablet keeps the latest one.
/// </remarks>
internal static class TabletCaptureReviewWiring
{
    public static void Attach(IServiceProvider services, V2ShellCaptureBridge captureBridge, TabletMapSurfacePublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(captureBridge);
        ArgumentNullException.ThrowIfNull(publisher);
        captureBridge.FleaScanShown += scan => publisher.ShowFleaReview(TabletCaptureReviewBuilder.FromFlea(scan));
        if (services.GetService<StashScanWorkspaceViewModel>() is not { } stash)
        {
            return;
        }

        stash.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(StashScanWorkspaceViewModel.Items) &&
                TabletCaptureReviewBuilder.FromStash(stash) is { } review)
            {
                publisher.ShowStashReview(review);
            }
        };
    }
}
